using Flash_Sale_Black_Friday.src.Infrastructure.DataLocalization;
using Flash_Sale_Black_Friday.src.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using StackExchange.Redis;
using System.Data;
using System.Diagnostics;
using System.Threading;

namespace FlashSale.Controllers
{
    /// <summary>
    ///     FLASH SALE CONTROLLER — 8 phương pháp concurrency control
    ///     
    ///     C4 - Horizontal Fragmentation: Orders & PurchaseLog → Node_Even / Node_Odd
    ///     C3 - Fragmentation Transparency: Mọi INSERT đều qua _repo (không if/else shard)
    /// </summary>
    [ApiController]
    [Route("api/flash-sale")]
    public class FlashSaleController : ControllerBase
    {
        // ⭐ MÁY PHÁT SỐ THỨ TỰ (Arrival Order) để chứng minh Ai đến trước / Ai đến sau
        private static int _globalArrivalOrder = 0;

        private readonly IShardingRouter _router;
        private readonly IDistributedOrderRepository _repo;
        private readonly ILogger<FlashSaleController> _logger;
        private readonly IConnectionMultiplexer _redis;

        public FlashSaleController(
            IShardingRouter router,
            IDistributedOrderRepository repo,
            ILogger<FlashSaleController> logger,
            IConnectionMultiplexer redis)
        {
            _router = router;
            _repo = repo;
            _logger = logger;
            _redis = redis;
        }

        // =============================================================
        //  0. RESET
        // =============================================================
        [HttpPost("reset")]
        public async Task<IActionResult> Reset([FromQuery] int productId = 1, [FromQuery] int stock = 1)
        {
            // Reset lại số thứ tự về 0 mỗi khi test lại
            Interlocked.Exchange(ref _globalArrivalOrder, 0);

            using (var conn = _router.OpenMasterConnection())
            using (var cmd = new SqlCommand("dbo.sp_ResetStock", conn))
            {
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.Parameters.AddWithValue("@ProductId", productId);
                cmd.Parameters.AddWithValue("@Stock", stock);
                await cmd.ExecuteNonQueryAsync();
            }

            using (var shardConn = _router.OpenShardConnection(productId))
            using (var cmd = new SqlCommand("dbo.sp_ResetShardData", shardConn))
            {
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.Parameters.AddWithValue("@ProductId", productId);
                await cmd.ExecuteNonQueryAsync();
            }

            // Reset Redis counter nếu có
            var redisKey = $"stock:product:{productId}";
            await _redis.GetDatabase().KeyDeleteAsync(redisKey);

            return Ok(new
            {
                Message = $"Đã reset ProductId={productId} về Stock={stock}",
                Shard = _router.ResolveShardName(productId),
                Note = "Orders/PurchaseLog + Redis counter đã reset."
            });
        }

        // =============================================================
        //  1. NO LOCK — BUG: overselling!
        // =============================================================
        [HttpPost("buy-no-lock")]
        public async Task<IActionResult> BuyNoLock([FromQuery] int productId = 1)
        {
            // Bốc số thứ tự ngay lập tức
            int arrivalOrder = Interlocked.Increment(ref _globalArrivalOrder);
            string serverTime = DateTime.UtcNow.ToString("HH:mm:ss.fffffff");

            var tid = $"T{Environment.CurrentManagedThreadId}";
            var sw = Stopwatch.StartNew();
            try
            {
                int stockBefore;
                decimal price;

                using (var conn = _router.OpenMasterConnection())
                using (var cmd = new SqlCommand(
                    "SELECT Stock, SalePrice FROM dbo.Products WITH (NOLOCK) WHERE ProductId=@p", conn))
                {
                    cmd.Parameters.AddWithValue("@p", productId);
                    using var r = await cmd.ExecuteReaderAsync();
                    if (!await r.ReadAsync())
                        return NotFound(new { Error = "Product not found" });
                    stockBefore = r.GetInt32(0);
                    price = r.GetDecimal(1);
                }

                if (stockBefore <= 0)
                {
                    await _repo.LogAsync(productId, tid, "NO_LOCK", "FAILED",
                        stockBefore, stockBefore, Convert.ToDecimal(sw.Elapsed.TotalMilliseconds), "Hết hàng");
                    return Conflict(new { Method = "NO_LOCK", Status = "FAILED", Reason = "Out of stock", ArrivalOrder = arrivalOrder, ServerTime = serverTime });
                }

                await Task.Delay(2);   // race window

                int stockAfter;
                using (var conn = _router.OpenMasterConnection())
                // ⚡ Sử dụng OUTPUT INSERTED.Stock để lấy ngay giá trị vừa bị trừ của luồng này
                using (var cmd = new SqlCommand(
                    "UPDATE dbo.Products SET Stock = Stock - 1 OUTPUT INSERTED.Stock WHERE ProductId=@p", conn))
                {
                    cmd.Parameters.AddWithValue("@p", productId);
                    stockAfter = (int)(await cmd.ExecuteScalarAsync())!;
                }

                await _repo.SaveOrderAsync(productId, tid, price, "SUCCESS", "NO_LOCK");

                sw.Stop();
                await _repo.LogAsync(productId, tid, "NO_LOCK", "SUCCESS",
                    stockBefore, stockAfter, Convert.ToDecimal(sw.Elapsed.TotalMilliseconds), "Mua OK");

                return Ok(new
                {
                    Method = "NO_LOCK",
                    Status = "SUCCESS",
                    Thread = tid,
                    ArrivalOrder = arrivalOrder,
                    ServerTime = serverTime,
                    StockBefore = stockBefore,
                    StockAfter = stockAfter,
                    Shard = _router.ResolveShardName(productId),
                    Warning = stockAfter < 0
                                  ? $"🚨 OVERSELLING! Stock={stockAfter}"
                                  : "⚠️ Có thể overselling nếu nhiều request song song."
                });
            }
            catch (Exception ex)
            {
                sw.Stop();
                await _repo.LogAsync(productId, tid, "NO_LOCK", "ERROR",
                    null, null, Convert.ToDecimal(sw.Elapsed.TotalMilliseconds), ex.Message);
                return StatusCode(500, new { Error = ex.Message, ArrivalOrder = arrivalOrder, ServerTime = serverTime });
            }
        }

        // =============================================================
        //  2. ATOMIC — UPDATE ... WHERE Stock > 0
        // =============================================================
        [HttpPost("buy-atomic")]
        public async Task<IActionResult> BuyAtomic([FromQuery] int productId = 1)
        {
            int arrivalOrder = Interlocked.Increment(ref _globalArrivalOrder);
            string serverTime = DateTime.UtcNow.ToString("HH:mm:ss.fffffff");

            var tid = $"T{Environment.CurrentManagedThreadId}";
            var sw = Stopwatch.StartNew();

            int rows;
            int stockBefore, stockAfter;
            decimal price;

            try
            {
                using (var conn = _router.OpenMasterConnection())
                {
                    using (var read = new SqlCommand(
                        "SELECT Stock, SalePrice FROM dbo.Products WHERE ProductId=@p", conn))
                    {
                        read.Parameters.AddWithValue("@p", productId);
                        using var r = await read.ExecuteReaderAsync();
                        if (!await r.ReadAsync()) return NotFound();
                        stockBefore = r.GetInt32(0);
                        price = r.GetDecimal(1);
                    }
                    await Task.Delay(8);
                    using var cmd = new SqlCommand(@"
                        UPDATE dbo.Products
                           SET Stock = Stock - 1, UpdatedAt = SYSUTCDATETIME()
                         WHERE ProductId = @p AND Stock > 0", conn);
                    cmd.Parameters.AddWithValue("@p", productId);
                    rows = await cmd.ExecuteNonQueryAsync();

                    using (var read2 = new SqlCommand(
                        "SELECT Stock FROM dbo.Products WHERE ProductId=@p", conn))
                    {
                        read2.Parameters.AddWithValue("@p", productId);
                        stockAfter = (int)(await read2.ExecuteScalarAsync())!;
                    }
                }

                sw.Stop();

                if (rows > 0)
                {
                    await _repo.SaveOrderAsync(productId, tid, price, "SUCCESS", "ATOMIC");
                    await _repo.LogAsync(productId, tid, "ATOMIC", "SUCCESS",
                        stockBefore, stockAfter, Convert.ToDecimal(sw.Elapsed.TotalMilliseconds), "Mua thành công");

                    return Ok(new
                    {
                        Method = "ATOMIC",
                        Status = "SUCCESS",
                        Thread = tid,
                        ArrivalOrder = arrivalOrder,
                        ServerTime = serverTime,
                        StockBefore = stockBefore,
                        StockAfter = stockAfter,
                        Shard = _router.ResolveShardName(productId),
                        Advantage = "✅ Không bao giờ overselling."
                    });
                }
                else
                {
                    await _repo.LogAsync(productId, tid, "ATOMIC", "FAILED",
                        stockBefore, stockAfter, Convert.ToDecimal(sw.Elapsed.TotalMilliseconds), "Hết hàng");
                    return Conflict(new
                    {
                        Method = "ATOMIC",
                        Status = "FAILED",
                        Reason = "Out of stock",
                        ArrivalOrder = arrivalOrder,
                        ServerTime = serverTime,
                        StockAfter = stockAfter,
                        Shard = _router.ResolveShardName(productId)
                    });
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                return StatusCode(500, new { Error = ex.Message, ArrivalOrder = arrivalOrder, ServerTime = serverTime });
            }
        }

        // =============================================================
        //  3. PESSIMISTIC LOCK — UPDLOCK, HOLDLOCK
        // =============================================================
        [HttpPost("buy-pessimistic")]
        public async Task<IActionResult> BuyPessimistic([FromQuery] int productId = 1)
        {
            int arrivalOrder = Interlocked.Increment(ref _globalArrivalOrder);
            string serverTime = DateTime.UtcNow.ToString("HH:mm:ss.fffffff");

            var tid = $"T{Environment.CurrentManagedThreadId}";
            var sw = Stopwatch.StartNew();

            using var conn = _router.OpenMasterConnection();
            using var tx = conn.BeginTransaction();
            try
            {
                int stockBefore;
                decimal price;
                await Task.Delay(25);

                using (var cmd = new SqlCommand(
                    "SELECT Stock, SalePrice FROM dbo.Products WITH (UPDLOCK, HOLDLOCK) WHERE ProductId=@p",
                    conn, tx))
                {
                    cmd.Parameters.AddWithValue("@p", productId);
                    using var r = await cmd.ExecuteReaderAsync();
                    if (!await r.ReadAsync()) { tx.Rollback(); return NotFound(); }
                    stockBefore = r.GetInt32(0);
                    price = r.GetDecimal(1);
                }

                if (stockBefore <= 0)
                {
                    tx.Rollback();
                    sw.Stop();
                    await _repo.LogAsync(productId, tid, "PESSIMISTIC", "FAILED",
                        stockBefore, stockBefore, Convert.ToDecimal(sw.Elapsed.TotalMilliseconds), "Hết hàng");
                    return Conflict(new { Method = "PESSIMISTIC", Status = "FAILED", Reason = "Out of stock", ArrivalOrder = arrivalOrder, ServerTime = serverTime });
                }

                using (var cmd = new SqlCommand(
                    "UPDATE dbo.Products SET Stock = Stock - 1 WHERE ProductId=@p", conn, tx))
                {
                    cmd.Parameters.AddWithValue("@p", productId);
                    await cmd.ExecuteNonQueryAsync();
                }

                int stockAfter = stockBefore - 1;
                await _repo.SaveOrderAsync(productId, tid, price, "SUCCESS", "PESSIMISTIC");
                tx.Commit();

                sw.Stop();
                await _repo.LogAsync(productId, tid, "PESSIMISTIC", "SUCCESS",
                    stockBefore, stockAfter, Convert.ToDecimal(sw.Elapsed.TotalMilliseconds), "Mua OK");

                return Ok(new
                {
                    Method = "PESSIMISTIC",
                    Status = "SUCCESS",
                    Thread = tid,
                    ArrivalOrder = arrivalOrder,
                    ServerTime = serverTime,
                    StockBefore = stockBefore,
                    StockAfter = stockAfter,
                    Shard = _router.ResolveShardName(productId),
                    Advantage = "✅ An toàn tuyệt đối.",
                    Drawback = "⚠️ Throughput thấp."
                });
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                sw.Stop();
                await _repo.LogAsync(productId, tid, "PESSIMISTIC", "ERROR",
                    null, null, Convert.ToDecimal(sw.Elapsed.TotalMilliseconds), ex.Message);
                return StatusCode(500, new { Error = ex.Message, ArrivalOrder = arrivalOrder, ServerTime = serverTime });
            }
        }

        // =============================================================
        //  4. OPTIMISTIC LOCK — Version check
        // =============================================================
        [HttpPost("buy-optimistic")]
        public async Task<IActionResult> BuyOptimistic([FromQuery] int productId = 1, [FromQuery] int maxRetry = 3)
        {
            int arrivalOrder = Interlocked.Increment(ref _globalArrivalOrder);
            string serverTime = DateTime.UtcNow.ToString("HH:mm:ss.fffffff");

            var tid = $"T{Environment.CurrentManagedThreadId}";
            var sw = Stopwatch.StartNew();
            int attempt = 0;

            try
            {
                while (attempt++ < maxRetry)
                {
                    int stockBefore, version;
                    decimal price;

                    using (var conn = _router.OpenMasterConnection())
                    using (var cmd = new SqlCommand(
                        "SELECT Stock, Version, SalePrice FROM dbo.Products WHERE ProductId=@p", conn))
                    {
                        cmd.Parameters.AddWithValue("@p", productId);
                        using var r = await cmd.ExecuteReaderAsync();
                        if (!await r.ReadAsync()) return NotFound();
                        stockBefore = r.GetInt32(0);
                        version = r.GetInt32(1);
                        price = r.GetDecimal(2);
                    }

                    if (stockBefore <= 0)
                    {
                        sw.Stop();
                        await _repo.LogAsync(productId, tid, "OPTIMISTIC", "FAILED",
                            stockBefore, stockBefore, Convert.ToDecimal(sw.Elapsed.TotalMilliseconds), "Hết hàng");
                        return Conflict(new { Method = "OPTIMISTIC", Status = "FAILED", Reason = "Out of stock", ArrivalOrder = arrivalOrder, ServerTime = serverTime });
                    }

                    int rows;
                    using (var conn = _router.OpenMasterConnection())
                    using (var cmd = new SqlCommand(@"
                        UPDATE dbo.Products
                           SET Stock   = Stock - 1,
                               Version = Version + 1,
                               UpdatedAt = SYSUTCDATETIME()
                         WHERE ProductId=@p AND Version=@v AND Stock>0", conn))
                    {
                        cmd.Parameters.AddWithValue("@p", productId);
                        cmd.Parameters.AddWithValue("@v", version);
                        rows = await cmd.ExecuteNonQueryAsync();
                    }

                    if (rows > 0)
                    {
                        await _repo.SaveOrderAsync(productId, tid, price, "SUCCESS", "OPTIMISTIC");
                        sw.Stop();
                        await _repo.LogAsync(productId, tid, "OPTIMISTIC", "SUCCESS",
                            stockBefore, stockBefore - 1, Convert.ToDecimal(sw.Elapsed.TotalMilliseconds),
                            $"Mua OK lần thử {attempt}/{maxRetry}");

                        return Ok(new
                        {
                            Method = "OPTIMISTIC",
                            Status = "SUCCESS",
                            Thread = tid,
                            ArrivalOrder = arrivalOrder,
                            ServerTime = serverTime,
                            Attempts = attempt,
                            VersionMatched = version,
                            StockBefore = stockBefore,
                            StockAfter = stockBefore - 1,
                            Shard = _router.ResolveShardName(productId),
                            Advantage = "✅ Không khóa — nhiều reader song song.",
                            Drawback = $"⚠️ Retry {attempt} lần."
                        });
                    }
                    await Task.Delay(Random.Shared.Next(5, 20));
                }

                sw.Stop();
                await _repo.LogAsync(productId, tid, "OPTIMISTIC", "FAILED",
                    null, null, Convert.ToDecimal(sw.Elapsed.TotalMilliseconds), $"Hết {maxRetry} lần retry");
                return Conflict(new
                {
                    Method = "OPTIMISTIC",
                    Status = "FAILED",
                    Reason = $"Hết {maxRetry} lần retry — version conflict liên tục",
                    ArrivalOrder = arrivalOrder,
                    ServerTime = serverTime
                });
            }
            catch (Exception ex)
            {
                sw.Stop();
                return StatusCode(500, new { Error = ex.Message, ArrivalOrder = arrivalOrder, ServerTime = serverTime });
            }
        }

        // =============================================================
        //  5. SERIALIZABLE — DEADLOCK (1205)
        // =============================================================
        [HttpPost("buy-serializable")]
        public async Task<IActionResult> BuySerializable([FromQuery] int productId = 1)
        {
            int arrivalOrder = Interlocked.Increment(ref _globalArrivalOrder);
            string serverTime = DateTime.UtcNow.ToString("HH:mm:ss.fffffff");

            var tid = $"T{Environment.CurrentManagedThreadId}";
            var sw = Stopwatch.StartNew();

            using var conn = _router.OpenMasterConnection();
            using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable);
            try
            {
                int stockBefore;
                decimal price;

                using (var cmd = new SqlCommand(
                    "SELECT Stock, SalePrice FROM dbo.Products WHERE ProductId=@p", conn, tx))
                {
                    cmd.Parameters.AddWithValue("@p", productId);
                    using var r = await cmd.ExecuteReaderAsync();
                    if (!await r.ReadAsync()) { tx.Rollback(); return NotFound(); }
                    stockBefore = r.GetInt32(0);
                    price = r.GetDecimal(1);
                }

                await Task.Delay(40);   // kéo dài transaction để chọc lỗi 1205

                if (stockBefore <= 0)
                {
                    tx.Rollback();
                    sw.Stop();
                    await _repo.LogAsync(productId, tid, "SERIALIZABLE", "FAILED",
                        stockBefore, stockBefore, Convert.ToDecimal(sw.Elapsed.TotalMilliseconds), "Hết hàng");
                    return Conflict(new { Method = "SERIALIZABLE", Status = "FAILED", Reason = "Out of stock", ArrivalOrder = arrivalOrder, ServerTime = serverTime });
                }

                using (var cmd = new SqlCommand(
                    "UPDATE dbo.Products SET Stock = Stock - 1 WHERE ProductId=@p", conn, tx))
                {
                    cmd.Parameters.AddWithValue("@p", productId);
                    await cmd.ExecuteNonQueryAsync();
                }

                await _repo.SaveOrderAsync(productId, tid, price, "SUCCESS", "SERIALIZABLE");
                tx.Commit();
                sw.Stop();

                await _repo.LogAsync(productId, tid, "SERIALIZABLE", "SUCCESS",
                    stockBefore, stockBefore - 1, Convert.ToDecimal(sw.Elapsed.TotalMilliseconds), "Mua OK");

                return Ok(new
                {
                    Method = "SERIALIZABLE",
                    Status = "SUCCESS",
                    Thread = tid,
                    ArrivalOrder = arrivalOrder,
                    ServerTime = serverTime,
                    StockBefore = stockBefore,
                    StockAfter = stockBefore - 1,
                    Shard = _router.ResolveShardName(productId),
                    Advantage = "✅ Isolation cao nhất.",
                    Drawback = "⚠️ Dễ văng deadlock (Error 1205)."
                });
            }
            catch (SqlException ex) when (ex.Number == 1205)
            {
                try { tx.Rollback(); } catch { }
                sw.Stop();
                await _repo.LogAsync(productId, tid, "SERIALIZABLE", "ERROR",
                    null, null, Convert.ToDecimal(sw.Elapsed.TotalMilliseconds), $"DEADLOCK 1205: {ex.Message}");
                return StatusCode(409, new
                {
                    Method = "SERIALIZABLE",
                    Status = "FAILED", // Báo failed để Frontend nhận diện
                    Reason = "🎯 Deadlock victim.",
                    ErrorNo = 1205,
                    ArrivalOrder = arrivalOrder,
                    ServerTime = serverTime,
                    Proof = "Bằng chứng nhược điểm Serializable.",
                    Detail = ex.Message
                });
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                sw.Stop();
                await _repo.LogAsync(productId, tid, "SERIALIZABLE", "ERROR",
                    null, null, Convert.ToDecimal(sw.Elapsed.TotalMilliseconds), ex.Message);
                return StatusCode(500, new { Error = ex.Message, ArrivalOrder = arrivalOrder, ServerTime = serverTime });
            }
        }

        // =============================================================
        //  6. REDIS RESERVED COUNTER
        // =============================================================
        [HttpPost("buy-reserved-counter")]
        public async Task<IActionResult> BuyReservedCounter([FromQuery] int productId = 1)
        {
            int arrivalOrder = Interlocked.Increment(ref _globalArrivalOrder);
            string serverTime = DateTime.UtcNow.ToString("HH:mm:ss.fffffff");

            var tid = $"T{Environment.CurrentManagedThreadId}";
            var sw = Stopwatch.StartNew();
            var redisKey = $"stock:product:{productId}";

            try
            {
                var redis = _redis.GetDatabase();

                // Khởi tạo counter từ DB nếu chưa có (ATOMIC)
                if (!await redis.KeyExistsAsync(redisKey))
                {
                    int dbStock;
                    using (var conn = _router.OpenMasterConnection())
                    using (var cmd = new SqlCommand("SELECT Stock FROM dbo.Products WHERE ProductId=@p", conn))
                    {
                        cmd.Parameters.AddWithValue("@p", productId);
                        dbStock = (int)(await cmd.ExecuteScalarAsync() ?? 0);
                    }

                    // SETNX: chỉ 1 thread set thành công
                    await redis.StringSetAsync(redisKey, dbStock, when: When.NotExists);

                    // Delay nhỏ để đảm bảo key đã sẵn sàng
                    await Task.Delay(1);
                }

                var newStock = await redis.StringDecrementAsync(redisKey);

                if (newStock < 0)
                {
                    await redis.StringIncrementAsync(redisKey);
                    sw.Stop();
                    await _repo.LogAsync(productId, tid, "RESERVED_COUNTER", "FAILED",
                        (int)newStock + 1, (int)newStock + 1,
                        Convert.ToDecimal(sw.Elapsed.TotalMilliseconds), "Hết hàng");
                    return Conflict(new
                    {
                        Method = "RESERVED_COUNTER",
                        Status = "FAILED",
                        Reason = "Out of stock (Redis counter)",
                        ArrivalOrder = arrivalOrder,
                        ServerTime = serverTime,
                        StockAfter = newStock + 1
                    });
                }

                decimal price;
                using (var conn = _router.OpenMasterConnection())
                {
                    using var cmd = new SqlCommand(
                        "UPDATE dbo.Products SET Stock=@s WHERE ProductId=@p;" +
                        "SELECT SalePrice FROM dbo.Products WHERE ProductId=@p;", conn);
                    cmd.Parameters.AddWithValue("@s", (int)newStock);
                    cmd.Parameters.AddWithValue("@p", productId);
                    price = (decimal)(await cmd.ExecuteScalarAsync())!;
                }

                await _repo.SaveOrderAsync(productId, tid, price, "SUCCESS", "RESERVED_COUNTER");

                sw.Stop();
                await _repo.LogAsync(productId, tid, "RESERVED_COUNTER", "SUCCESS",
                    (int)newStock + 1, (int)newStock,
                    Convert.ToDecimal(sw.Elapsed.TotalMilliseconds), "Mua OK (Redis counter)");

                return Ok(new
                {
                    Method = "RESERVED_COUNTER",
                    Status = "SUCCESS",
                    Thread = tid,
                    ArrivalOrder = arrivalOrder,
                    ServerTime = serverTime,
                    StockAfter = newStock, // Chuẩn hóa trả về Frontend
                    Shard = _router.ResolveShardName(productId),
                    Advantage = "✅ Throughput cao — Redis DECR nhanh.",
                    Drawback = "⚠️ Eventual consistency."
                });
            }
            catch (Exception ex)
            {
                sw.Stop();
                await _repo.LogAsync(productId, tid, "RESERVED_COUNTER", "ERROR",
                    null, null, Convert.ToDecimal(sw.Elapsed.TotalMilliseconds), ex.Message);
                return StatusCode(500, new { Error = ex.Message, ArrivalOrder = arrivalOrder, ServerTime = serverTime });
            }
        }

        // =============================================================
        //  7. DISTRIBUTED LOCK (Redis SETNX)
        // =============================================================
        [HttpPost("buy-distributed-lock")]
        public async Task<IActionResult> BuyDistributedLock([FromQuery] int productId = 1)
        {
            int arrivalOrder = Interlocked.Increment(ref _globalArrivalOrder);
            string serverTime = DateTime.UtcNow.ToString("HH:mm:ss.fffffff");

            var tid = $"T{Environment.CurrentManagedThreadId}";
            var sw = Stopwatch.StartNew();
            var lockKey = $"lock:product:{productId}";
            var lockValue = Guid.NewGuid().ToString();
            var redis = _redis.GetDatabase();

            bool acquired = false;
            try
            {
                for (int attempt = 0; attempt < 50; attempt++)
                {
                    acquired = await redis.StringSetAsync(lockKey, lockValue,
                        TimeSpan.FromSeconds(5), When.NotExists);
                    if (acquired) break;
                    await Task.Delay(10);
                }

                if (!acquired)
                {
                    sw.Stop();
                    await _repo.LogAsync(productId, tid, "DISTRIBUTED_LOCK", "FAILED",
                        null, null, Convert.ToDecimal(sw.Elapsed.TotalMilliseconds),
                        "Timeout — không lấy được lock");
                    return StatusCode(408, new
                    {
                        Method = "DISTRIBUTED_LOCK",
                        Status = "FAILED",
                        Reason = "Không lấy được distributed lock sau 500ms",
                        ArrivalOrder = arrivalOrder,
                        ServerTime = serverTime,
                        Drawback = "⚠️ Spinlock tốn CPU."
                    });
                }

                int stockBefore, stockAfter;
                decimal price;
                using (var conn = _router.OpenMasterConnection())
                {
                    using var cmd = new SqlCommand(
                        "SELECT Stock, SalePrice FROM dbo.Products WHERE ProductId=@p", conn);
                    cmd.Parameters.AddWithValue("@p", productId);
                    using var r = await cmd.ExecuteReaderAsync();
                    if (!await r.ReadAsync()) return NotFound();
                    stockBefore = r.GetInt32(0);
                    price = r.GetDecimal(1);
                }

                if (stockBefore <= 0)
                {
                    sw.Stop();
                    await _repo.LogAsync(productId, tid, "DISTRIBUTED_LOCK", "FAILED",
                        stockBefore, stockBefore, Convert.ToDecimal(sw.Elapsed.TotalMilliseconds),
                        "Hết hàng");
                    return Conflict(new
                    {
                        Method = "DISTRIBUTED_LOCK",
                        Status = "FAILED",
                        Reason = "Out of stock",
                        ArrivalOrder = arrivalOrder,
                        ServerTime = serverTime,
                    });
                }

                using (var conn = _router.OpenMasterConnection())
                using (var cmd = new SqlCommand(
                    "UPDATE dbo.Products SET Stock = Stock - 1 WHERE ProductId=@p", conn))
                {
                    cmd.Parameters.AddWithValue("@p", productId);
                    await cmd.ExecuteNonQueryAsync();
                }

                stockAfter = stockBefore - 1;
                await _repo.SaveOrderAsync(productId, tid, price, "SUCCESS", "DISTRIBUTED_LOCK");

                sw.Stop();
                await _repo.LogAsync(productId, tid, "DISTRIBUTED_LOCK", "SUCCESS",
                    stockBefore, stockAfter, Convert.ToDecimal(sw.Elapsed.TotalMilliseconds),
                    "Mua OK (Distributed Lock)");

                return Ok(new
                {
                    Method = "DISTRIBUTED_LOCK",
                    Status = "SUCCESS",
                    Thread = tid,
                    ArrivalOrder = arrivalOrder,
                    ServerTime = serverTime,
                    StockBefore = stockBefore,
                    StockAfter = stockAfter,
                    Shard = _router.ResolveShardName(productId),
                    Advantage = "✅ Lock toàn cục cross-server.",
                    Drawback = "⚠️ Spinlock tốn CPU."
                });
            }
            catch (Exception ex)
            {
                sw.Stop();
                await _repo.LogAsync(productId, tid, "DISTRIBUTED_LOCK", "ERROR",
                    null, null, Convert.ToDecimal(sw.Elapsed.TotalMilliseconds), ex.Message);
                return StatusCode(500, new { Error = ex.Message, ArrivalOrder = arrivalOrder, ServerTime = serverTime });
            }
            finally
            {
                if (acquired)
                {
                    var script = @"
                        if redis.call('get', KEYS[1]) == ARGV[1] then
                            return redis.call('del', KEYS[1])
                        else
                            return 0
                        end";
                    await redis.ScriptEvaluateAsync(script, new RedisKey[] { lockKey },
                                                    new RedisValue[] { lockValue });
                }
            }
        }

        // =============================================================
        //  8. MESSAGE QUEUE (Redis List)
        // =============================================================
        [HttpPost("buy-queue")]
        public async Task<IActionResult> BuyQueue([FromQuery] int productId = 1)
        {
            int arrivalOrder = Interlocked.Increment(ref _globalArrivalOrder);
            string serverTime = DateTime.UtcNow.ToString("HH:mm:ss.fffffff");

            var tid = $"T{Environment.CurrentManagedThreadId}";
            var sw = Stopwatch.StartNew();
            var queueKey = $"queue:purchase:{productId}";
            var counterKey = $"stock:product:{productId}";

            try
            {
                var redis = _redis.GetDatabase();

                // ⭐ BƯỚC 1: Khởi tạo Redis counter từ DB (ATOMIC với SETNX)
                // FIX RACE CONDITION: Dùng StringSetAsync với When.NotExists
                var counterExists = await redis.KeyExistsAsync(counterKey);

                if (!counterExists)
                {
                    // Đọc stock từ DB
                    int dbStock;
                    using (var conn = _router.OpenMasterConnection())
                    using (var cmd = new SqlCommand(
                        "SELECT Stock FROM dbo.Products WHERE ProductId=@p", conn))
                    {
                        cmd.Parameters.AddWithValue("@p", productId);
                        dbStock = (int)(await cmd.ExecuteScalarAsync() ?? 0);
                    }

                    // ⭐ SETNX (Set if Not Exists): CHỈ 1 thread set thành công
                    // Các thread khác thấy key đã có → bỏ qua
                    await redis.StringSetAsync(counterKey, dbStock, when: When.NotExists);

                    // Đợi chút để đảm bảo key đã được set (tránh race với DECR)
                    await Task.Delay(1);
                }

                // BƯỚC 2: Atomic DECR để reserve slot
                var newStock = await redis.StringDecrementAsync(counterKey);

                if (newStock < 0)
                {
                    // Rollback Redis counter
                    await redis.StringIncrementAsync(counterKey);
                    sw.Stop();
                    await _repo.LogAsync(productId, tid, "QUEUE", "FAILED",
                        (int)newStock + 1, (int)newStock + 1,
                        Convert.ToDecimal(sw.Elapsed.TotalMilliseconds), "Hết hàng — từ chối vào queue");

                    return Conflict(new
                    {
                        Method = "QUEUE",
                        Status = "FAILED",
                        Thread = tid,
                        ArrivalOrder = arrivalOrder,
                        ServerTime = serverTime,
                        Reason = "Out of stock",
                        Message = "Stock không đủ, request bị từ chối trước khi vào queue.",
                        Note = "Queue architecture với pre-check: chỉ request hợp lệ mới được vào queue."
                    });
                }

                // ⭐ BƯỚC 2: Push vào queue (đã reserve slot thành công)
                var request = new
                {
                    ProductId = productId,
                    ThreadId = tid,
                    Timestamp = DateTime.UtcNow,
                    ReservedStock = (int)newStock
                };
                await redis.ListRightPushAsync(queueKey,
                    System.Text.Json.JsonSerializer.Serialize(request));

                sw.Stop();
                await _repo.LogAsync(productId, tid, "QUEUE", "SUCCESS",
                    (int)newStock + 1, (int)newStock,
                    Convert.ToDecimal(sw.Elapsed.TotalMilliseconds),
                    "Request đã vào queue với stock reserved");

                return Accepted(new
                {
                    Method = "QUEUE",
                    Status = "QUEUED",
                    Thread = tid,
                    ArrivalOrder = arrivalOrder,
                    ServerTime = serverTime,
                    QueueKey = queueKey,
                    ReservedStock = (int)newStock,
                    Message = "✅ Request đã được xếp hàng. Worker sẽ xử lý async.",
                    Note = "Demo có pre-check stock qua Redis. Chỉ 1 request vào queue, 19 bị reject.",
                    Advantage = "✅ Throughput cao + không overselling.",
                    Drawback = "⚠️ Eventual consistency — worker có thể fail khi xử lý."
                });
            }
            catch (Exception ex)
            {
                sw.Stop();
                await _repo.LogAsync(productId, tid, "QUEUE", "ERROR",
                    null, null, Convert.ToDecimal(sw.Elapsed.TotalMilliseconds), ex.Message);
                return StatusCode(500, new
                {
                    Error = ex.Message,
                    ArrivalOrder = arrivalOrder,
                    ServerTime = serverTime
                });
            }
        }

        // =============================================================
        //  ⭐ GLOBAL QUERY — C4 Decomposition demo
        // =============================================================
        [HttpGet("total-revenue")]
        public async Task<IActionResult> GetTotalRevenue()
        {
            var sw = Stopwatch.StartNew();
            var result = await _repo.GetTotalRevenueAsync();
            sw.Stop();

            return Ok(new
            {
                GlobalQuery = "SELECT SUM(Quantity * UnitPrice) FROM Orders WHERE Status='SUCCESS'",
                Decomposition = new
                {
                    Strategy = "C4 - Horizontal Fragmentation with parallel execution",
                    Predicate = "ProductId % 2  →  Node_Even / Node_Odd",
                    FragmentQueries = result.Fragments.Select(f => new {
                        f.NodeName,
                        FragmentQuery = "SELECT SUM(Quantity*UnitPrice), COUNT(*) FROM Orders WHERE Status='SUCCESS'",
                        f.Revenue,
                        f.OrderCount,
                        f.DurationMs
                    })
                },
                Reconstruction = new
                {
                    Formula = "TotalRevenue = Σ fragment_i.Revenue  (disjoint fragments)",
                    TotalRevenue = result.TotalRevenue,
                    TotalOrderCount = result.TotalOrderCount
                },
                TotalDurationMs = Convert.ToDecimal(sw.Elapsed.TotalMilliseconds),
                Note = "Fragments được thực thi SONG SONG (Task.WhenAll)."
            });
        }

        // =============================================================
        //  Tiện ích: Xem đơn hàng 1 shard
        // =============================================================
        [HttpGet("orders/{productId:int}")]
        public async Task<IActionResult> GetOrders(int productId)
        {
            using var conn = _router.OpenShardConnection(productId);
            using var cmd = new SqlCommand(
                "SELECT OrderId, ThreadId, UnitPrice, Status, Method, CreatedAt " +
                "FROM dbo.Orders WHERE ProductId=@p ORDER BY OrderId", conn);
            cmd.Parameters.AddWithValue("@p", productId);

            var list = new List<object>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                list.Add(new
                {
                    OrderId = r.GetInt32(0),
                    ThreadId = r.GetString(1),
                    UnitPrice = r.GetDecimal(2),
                    Status = r.GetString(3),
                    Method = r.GetString(4),
                    CreatedAt = r.GetDateTime(5),
                });
            }

            return Ok(new
            {
                ProductId = productId,
                Shard = _router.ResolveShardName(productId),
                Note = "Dữ liệu CHỈ có ở shard tương ứng — bằng chứng C4.",
                Count = list.Count,
                Orders = list
            });
        }
    }
}