using Dapper;
using Microsoft.Data.SqlClient;
using StackExchange.Redis;
using System.Text.Json;

namespace Services;

public class RedisQueueWorker : BackgroundService
{
    private readonly IConnectionMultiplexer _redisConn;
    private readonly string _masterConn;
    private readonly ILogger<RedisQueueWorker> _logger;

    public RedisQueueWorker(IConnectionMultiplexer redisConn, IConfiguration config, ILogger<RedisQueueWorker> logger)
    {
        _redisConn = redisConn;
        _masterConn = config.GetConnectionString("MasterDb") ?? "";
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var db = _redisConn.GetDatabase();
        _logger.LogInformation("🚀 Redis Queue Worker đã khởi động...");

        // 🟢 FIX: Lắng nghe đúng Key mà API Controller đang đẩy vào
        string queueKey = "queue:purchase:1";

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await db.ListLeftPopAsync(queueKey);

                if (string.IsNullOrEmpty(job))
                {
                    await Task.Delay(50, stoppingToken); // Queue rỗng → nghỉ 50ms rồi quét lại
                    continue;
                }

                // 🟢 FIX: Parse đúng định dạng JSON mà API đã đẩy vào
                var doc = JsonDocument.Parse(job.ToString());
                int productId = doc.RootElement.GetProperty("ProductId").GetInt32();
                string threadId = doc.RootElement.GetProperty("ThreadId").GetString() ?? "";

                // Xử lý đơn hàng xuống DB
                await ProcessOrderAtomic(productId, threadId);
            }
            catch (Exception ex)
            {
                _logger.LogError("Queue Worker Lỗi: {Message}", ex.Message);
            }
        }
    }

    /// <summary>
    /// Xử lý đơn hàng bằng Atomic Update. 
    /// Nhanh và an toàn 100% không bao giờ Oversell.
    /// </summary>
    private async Task ProcessOrderAtomic(int productId, string threadId)
    {
        try
        {
            await using var con = new SqlConnection(_masterConn);
            await con.OpenAsync();

            // 🟢 TỐI ƯU: Không cần SELECT kiểm tra trước. Đập thẳng lệnh UPDATE kiểm tra Stock > 0.
            // Nếu hết hàng thì UPDATE trả về 0 dòng, snap sẽ bị null.
            var snap = await con.QueryFirstOrDefaultAsync<StockSnapshot>(
                @"UPDATE Products 
                  SET Stock = Stock - 1, UpdatedAt = SYSUTCDATETIME()
                  OUTPUT INSERTED.Stock, INSERTED.UpdatedAt
                  WHERE ProductId = @ProductId AND Stock > 0",
                new { ProductId = productId });

            if (snap != null) // Kho > 0 và đã trừ kho thành công
            {
                // Ghi nhận đơn hàng thành công
                await con.ExecuteAsync(
                    @"INSERT INTO Orders (ProductId, ThreadId, Quantity, UnitPrice, Status, Method)
                      SELECT @ProductId, @ThreadId, 1, SalePrice, 'SUCCESS', 'QUEUE' 
                      FROM Products WHERE ProductId = @ProductId",
                    new { ProductId = productId, ThreadId = threadId });

                _logger.LogInformation("✅ Worker trừ kho OK cho {ThreadId}. Kho còn: {Stock}", threadId, snap.Stock);
            }
            else
            {
                // Đơn hàng văng ra do hết hàng
                _logger.LogWarning("❌ Worker rớt đơn của {ThreadId} do HẾT HÀNG.", threadId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Lỗi Worker khi ghi DB cho {ThreadId}: {Message}", threadId, ex.Message);
        }
    }

    // DTO để Dapper map kết quả OUTPUT INSERTED
    private sealed class StockSnapshot
    {
        public int Stock { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}