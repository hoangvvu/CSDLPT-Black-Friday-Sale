using Dapper;
using Microsoft.Data.SqlClient;
using StackExchange.Redis;
using System.Text.Json;

namespace Services;

public class RedisQueueWorker : BackgroundService
{
    private readonly IConnectionMultiplexer _redisConn;
    private readonly string _masterConn;
    private readonly string _oddConn;    
    private readonly string _evenConn;
    private readonly ILogger<RedisQueueWorker> _logger;

    public RedisQueueWorker(IConnectionMultiplexer redisConn, IConfiguration config, ILogger<RedisQueueWorker> logger)
    {
        _redisConn = redisConn;
        _masterConn = config.GetConnectionString("Master") ?? "";
        _oddConn = config.GetConnectionString("Node_Odd") ?? "";   // thêm
        _evenConn = config.GetConnectionString("Node_Even") ?? "";  // thêm
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

    private async Task ProcessOrderAtomic(int productId, string threadId)
    {
        try
        {
            // Master: trừ stock
            await using var masterCon = new SqlConnection(_masterConn);
            await masterCon.OpenAsync();

            var snap = await masterCon.QueryFirstOrDefaultAsync<StockSnapshot>(
                @"UPDATE Products 
              SET Stock = Stock - 1, UpdatedAt = SYSUTCDATETIME()
              OUTPUT INSERTED.Stock, INSERTED.UpdatedAt
              WHERE ProductId = @ProductId AND Stock > 0",
                new { ProductId = productId });

            if (snap != null)
            {
                // Lấy SalePrice từ Master trước
                var price = await masterCon.QueryFirstAsync<decimal>(
                    "SELECT SalePrice FROM dbo.Products WHERE ProductId = @ProductId",
                    new { ProductId = productId });

                // INSERT vào đúng shard với giá đã lấy được
                var shardConnStr = productId % 2 == 1 ? _oddConn : _evenConn;
                await using var shardCon = new SqlConnection(shardConnStr);
                await shardCon.OpenAsync();

                await shardCon.ExecuteAsync(
                    @"INSERT INTO dbo.Orders (ProductId, ThreadId, Quantity, UnitPrice, Status, Method)
          VALUES (@ProductId, @ThreadId, 1, @Price, 'SUCCESS', 'QUEUE')",
                    new { ProductId = productId, ThreadId = threadId, Price = price });

                _logger.LogInformation("✅ Worker trừ kho OK cho {ThreadId}. Kho còn: {Stock}", threadId, snap.Stock);
            }
            else
            {
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