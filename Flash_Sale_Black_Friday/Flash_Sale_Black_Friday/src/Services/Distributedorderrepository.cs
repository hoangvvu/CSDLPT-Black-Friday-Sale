using Microsoft.Data.SqlClient;
using System.Data;
using Flash_Sale_Black_Friday.src.Infrastructure.DataLocalization;

namespace Flash_Sale_Black_Friday.src.Services
{
    /// <summary>
    /// C3 - Fragmentation Transparency ở tầng repository.
    ///
    /// Controller chỉ gọi SaveOrderAsync / LogAsync / GetTotalRevenueAsync
    /// mà tuyệt đối KHÔNG có if/else chọn node nào. Toàn bộ việc chọn
    /// shard được ủy thác cho IShardingRouter.
    /// </summary>
    public interface IDistributedOrderRepository
    {
        Task SaveOrderAsync(int productId, string threadId, decimal unitPrice,
                            string status, string method,
                            SqlConnection? sharedConn = null, SqlTransaction? sharedTx = null);

        Task LogAsync(int productId, string threadId, string method, string action,
                      int? stockBefore, int? stockAfter, decimal? durationMs, string? message,
                      SqlConnection? sharedConn = null, SqlTransaction? sharedTx = null);

        Task<GlobalRevenueResult> GetTotalRevenueAsync();
    }

    public class DistributedOrderRepository : IDistributedOrderRepository
    {
        private readonly IShardingRouter _router;
        public DistributedOrderRepository(IShardingRouter router) => _router = router;

        // ==========================================================
        //  LƯU ĐƠN HÀNG — FRAGMENTATION TRANSPARENT
        // ==========================================================
        public async Task SaveOrderAsync(int productId, string threadId, decimal unitPrice,
                                         string status, string method,
                                         SqlConnection? sharedConn = null, SqlTransaction? sharedTx = null)
        {
            const string sql = @"
                INSERT INTO dbo.Orders (ProductId, ThreadId, UnitPrice, Status, Method)
                VALUES (@pid, @tid, @price, @status, @method);";

            if (sharedConn != null)
            {
                using var cmd = new SqlCommand(sql, sharedConn, sharedTx);
                BindOrderParams(cmd, productId, threadId, unitPrice, status, method);
                await cmd.ExecuteNonQueryAsync();
                return;
            }

            using var conn = _router.OpenShardConnection(productId);
            using var cmd2 = new SqlCommand(sql, conn);
            BindOrderParams(cmd2, productId, threadId, unitPrice, status, method);
            await cmd2.ExecuteNonQueryAsync();
        }

        private static void BindOrderParams(SqlCommand cmd, int pid, string tid,
                                            decimal price, string status, string method)
        {
            cmd.Parameters.AddWithValue("@pid", pid);
            cmd.Parameters.AddWithValue("@tid", tid);
            cmd.Parameters.AddWithValue("@price", price);
            cmd.Parameters.AddWithValue("@status", status);
            cmd.Parameters.AddWithValue("@method", method);
        }

        // ==========================================================
        //  GHI LOG — FRAGMENTATION TRANSPARENT
        // ==========================================================
        public async Task LogAsync(int productId, string threadId, string method, string action,
                                   int? stockBefore, int? stockAfter, decimal? durationMs, string? message,
                                   SqlConnection? sharedConn = null, SqlTransaction? sharedTx = null)
        {
            const string sql = @"
                INSERT INTO dbo.PurchaseLog
                    (ProductId, ThreadId, Method, Action, StockBefore, StockAfter, Duration_Ms, Message)
                VALUES
                    (@pid, @tid, @method, @action, @sb, @sa, @dur, @msg);";

            if (sharedConn != null)
            {
                using var cmd = new SqlCommand(sql, sharedConn, sharedTx);
                BindLogParams(cmd, productId, threadId, method, action, stockBefore, stockAfter, durationMs, message);
                await cmd.ExecuteNonQueryAsync();
                return;
            }

            using var conn = _router.OpenShardConnection(productId);
            using var cmd2 = new SqlCommand(sql, conn);
            BindLogParams(cmd2, productId, threadId, method, action, stockBefore, stockAfter, durationMs, message);
            await cmd2.ExecuteNonQueryAsync();
        }

        private static void BindLogParams(SqlCommand cmd, int pid, string tid, string method,
                                          string action, int? sb, int? sa, decimal? dur, string? msg)
        {
            cmd.Parameters.AddWithValue("@pid", pid);
            cmd.Parameters.AddWithValue("@tid", tid);
            cmd.Parameters.AddWithValue("@method", method);
            cmd.Parameters.AddWithValue("@action", action);
            cmd.Parameters.AddWithValue("@sb", (object?)sb ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@sa", (object?)sa ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@dur", (object?)dur ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@msg", (object?)msg ?? DBNull.Value);
        }

        // ==========================================================
        //  GLOBAL QUERY — C4: QUERY DECOMPOSITION & RECONSTRUCTION
        // ==========================================================
        /// <summary>
        ///     Global Query:
        ///         SELECT SUM(Quantity * UnitPrice) FROM Orders WHERE Status='SUCCESS'
        ///
        ///     ──┬── Fragment Query gửi xuống Node_Even (song song)
        ///       └── Fragment Query gửi xuống Node_Odd  (song song)
        ///
        ///     Reconstruction: total = Σ fragment_i
        /// </summary>
        public async Task<GlobalRevenueResult> GetTotalRevenueAsync()
        {
            // ⚠️ CAST sang DECIMAL(18,2) NGAY TRONG ISNULL để kiểu trả về
            //    luôn là DECIMAL. Nếu chỉ ISNULL(SUM(x*y), 0) thì SQL Server
            //    suy kiểu từ '0' (INT), reader.GetDecimal() sẽ throw InvalidCastException
            //    khi shard trống — một bug kinh điển.
            const string fragmentSql = @"
                SELECT
                    ISNULL(SUM(CAST(Quantity AS DECIMAL(18,2)) * UnitPrice), CAST(0 AS DECIMAL(18,2))) AS Revenue,
                    COUNT(*) AS OrderCount
                FROM dbo.Orders
                WHERE Status = 'SUCCESS';";

            // --- Gửi fragment query SONG SONG xuống tất cả shard ---
            var tasks = _router.AllShards.Select(async shard =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await using var conn = new SqlConnection(shard.ConnectionString);
                await conn.OpenAsync();
                await using var cmd = new SqlCommand(fragmentSql, conn);
                await using var r = await cmd.ExecuteReaderAsync();

                decimal rev = 0m;
                int cnt = 0;
                if (await r.ReadAsync())
                {
                    // Đọc an toàn qua object để tránh vỡ kiểu nếu driver suy khác.
                    rev = r.IsDBNull(0) ? 0m : Convert.ToDecimal(r.GetValue(0));
                    cnt = r.IsDBNull(1) ? 0 : Convert.ToInt32(r.GetValue(1));
                }
                sw.Stop();

                return new FragmentRevenue(
                    NodeName: shard.NodeName,
                    Revenue: rev,
                    OrderCount: cnt,
                    DurationMs: sw.Elapsed.TotalMilliseconds);
            }).ToArray();

            var fragments = await Task.WhenAll(tasks);

            // --- Reconstruction: disjoint fragments → cộng trực tiếp ---
            return new GlobalRevenueResult(
                TotalRevenue: fragments.Sum(f => f.Revenue),
                TotalOrderCount: fragments.Sum(f => f.OrderCount),
                Fragments: fragments);
        }
    }

    // ==============================================================
    //  DTO cho API /total-revenue
    // ==============================================================
    public record FragmentRevenue(
        string NodeName,
        decimal Revenue,
        int OrderCount,
        double DurationMs);

    public record GlobalRevenueResult(
        decimal TotalRevenue,
        int TotalOrderCount,
        FragmentRevenue[] Fragments);
}