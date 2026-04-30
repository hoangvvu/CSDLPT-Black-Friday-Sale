using System.Data;
using System.Diagnostics;
using Dapper;
using Infrastructure.DataLocalization;
using Infrastructure.Persistence;
using Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Services;

public class FlashSaleService : IFlashSaleService
{
    private readonly MasterDbContext _masterCtx;
    private readonly IConnectionResolver _resolver;
    private readonly ILogger<FlashSaleService> _logger;

    private static readonly Random _random = new Random();
    public FlashSaleService(
        MasterDbContext masterCtx,
        IConnectionResolver resolver,
        ILogger<FlashSaleService> logger)
    {
        _masterCtx = masterCtx;
        _resolver = resolver;
        _logger = logger;
    }

    // ═══════════════════════════════════════════════════════════════
    // Helper: lấy connection string Master / Slave
    // ═══════════════════════════════════════════════════════════════
    private string MasterConnStr => _resolver.Resolve(NodeRoute.Node1Master);
    private string SlaveConnStr => _resolver.Resolve(NodeRoute.Node1Slave);

    // ═══════════════════════════════════════════════════════════════
    // 1. BUY NO LOCK — DEMO OVERSELL THỰC TẾ
    //
    //    Cơ chế gây Oversell:
    //      - Tất cả 20 thread ĐỌC Stock từ SLAVE cùng lúc → đều thấy Stock = 1
    //      - Delay 100ms (giả lập network lag / business logic)
    //      - Tất cả cùng GHI lên MASTER: SET Stock = @StaleValue - 1
    //        ★ KHÔNG dùng Stock = Stock - 1 (vì DB engine vẫn serialize phép trừ)
    //        ★ KHÔNG dùng WHERE Stock > 0 (vì đây là ràng buộc ngầm chống Oversell)
    //        → Mỗi thread đều ghi Stock = 0 (từ giá trị cũ 1 - 1 = 0)
    //        → 20 đơn hàng SUCCESS nhưng thực tế chỉ có 1 sản phẩm = OVERSELL!
    //
    //    Lưu ý: Cần DROP CHECK constraint trên cột Stock trước khi chạy.
    //           Xem sp_ResetFlashSale_NoLock hoặc gọi endpoint /reset-nolock.
    // ═══════════════════════════════════════════════════════════════
    public async Task<BuyResult> BuyNoLockAsync(int productId, string threadId)
    {
        const string method = "NO_LOCK";
        var sw = Stopwatch.StartNew();

        try
        {
            // ⚡ MẸO 1: STAGGER START (Dãn hàng đợi đầu vào)
            // Thay vì 20 đứa cùng đọc Slave 1 lúc, ta dãn tụi nó ra trong khoảng 0-150ms.
            // Điều này giúp "đóng cửa" Slave kịp lúc để chặn các thread đến sau.
            await Task.Delay(_random.Next(0, 150));

            // ── BƯỚC 1: ĐỌC TỪ SLAVE ──────────────────────────────────────────
            int stockFromSlave;
            decimal salePrice;

            await using (var slaveCon = new SqlConnection(SlaveConnStr))
            {
                await slaveCon.OpenAsync();
                var product = await slaveCon.QueryFirstOrDefaultAsync<dynamic>(
                    "SELECT Stock, SalePrice FROM Products WITH (NOLOCK) WHERE ProductId = @ProductId",
                    new { ProductId = productId });

                if (product is null) return Result(threadId, method, false, "Không tồn tại", sw);

                stockFromSlave = (int)product.Stock;
                salePrice = (decimal)product.SalePrice;
            }

            // CHẶN: Nếu Slave đã nhận được tin Master hết hàng (Stock <= 0)
            if (stockFromSlave <= 0)
                return Result(threadId, method, false, "Hết hàng (Slave đã đóng cửa)", sw);

            // ── BƯỚC 2: GIẢ LẬP REPLICATION LAG (Đồng bộ Master -> Slave) ──────
            // Rút ngắn thời gian đồng bộ để "đua" với các thread đang dãn hàng ở Bước 1
            _ = Task.Run(async () => {
                try
                {
                    await Task.Delay(_random.Next(20, 80)); // Đồng bộ nhanh hơn (20-80ms)

                    int realStock;
                    using (var mCon = new SqlConnection(MasterConnStr))
                        realStock = await mCon.ExecuteScalarAsync<int>("SELECT Stock FROM Products WHERE ProductId = @id", new { id = productId });

                    using (var sCon = new SqlConnection(SlaveConnStr))
                        await sCon.ExecuteAsync("UPDATE Products SET Stock = @s WHERE ProductId = @id", new { s = realStock, id = productId });
                }
                catch { }
            });

            // ── BƯỚC 3: GHI LÊN MASTER ─────────────────────────────────────────
            await using var masterCon = new SqlConnection(MasterConnStr);
            await masterCon.OpenAsync();

            // Trừ kho trực tiếp trên Master
            await masterCon.ExecuteAsync(
                @"UPDATE Products SET Stock = Stock - 1, UpdatedAt = SYSUTCDATETIME() 
              WHERE ProductId = @ProductId",
                new { ProductId = productId });

            int stockAfter = await masterCon.ExecuteScalarAsync<int>(
                "SELECT Stock FROM Products WHERE ProductId = @ProductId", new { ProductId = productId });

            // Ghi Log và kết quả
            await LogPurchaseAsync(productId, threadId, method, "SUCCESS", stockFromSlave, stockAfter, ElapsedMs(sw),
                stockAfter < 0 ? $"OVERSELL! (Hàng âm: {stockAfter})" : "Mua thành công");

            string statusMsg = stockAfter < 0
                ? $"Thành công — OVERSELL! (Kho Master hiện tại: {stockAfter})"
                : "Thành công (Đúng tồn kho)";

            return Result(threadId, method, true, statusMsg, sw);
        }
        catch (Exception ex)
        {
            return Result(threadId, method, false, $"Lỗi: {ex.Message}", sw);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 2. BUY ATOMIC
    //    Gọi stored procedure sp_Purchase_Atomic trên MASTER
    // ═══════════════════════════════════════════════════════════════
    public async Task<BuyResult> BuyAtomicAsync(int productId, string threadId)
    {
        const string method = "ATOMIC";
        var sw = Stopwatch.StartNew();

        try
        {
            await using var masterCon = new SqlConnection(MasterConnStr);
            await masterCon.OpenAsync();

            await masterCon.ExecuteAsync(
                "EXEC dbo.sp_Purchase_Atomic @ProductId = @ProductId, @ThreadId = @ThreadId",
                new { ProductId = productId, ThreadId = threadId });

            // Kiểm tra kết quả qua PurchaseLog — lấy record mới nhất của thread này
            var log = await masterCon.QueryFirstOrDefaultAsync<dynamic>(
                @"SELECT TOP 1 Action, Message
                  FROM PurchaseLog
                  WHERE ProductId = @ProductId AND ThreadId = @ThreadId AND Method = 'ATOMIC'
                  ORDER BY LogId DESC",
                new { ProductId = productId, ThreadId = threadId });

            bool success = log?.Action == "SUCCESS";
            string message = log?.Message ?? "Không rõ kết quả";

            return Result(threadId, method, success, message, sw);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ATOMIC] Lỗi.");
            return Result(threadId, method, false, $"Lỗi hệ thống: {ex.Message}", sw);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 3. BUY PESSIMISTIC LOCK
    //    Dapper + SELECT WITH (UPDLOCK, ROWLOCK) trên MASTER
    //    Row bị khóa ngay khi đọc → thread khác BLOCK cho đến khi COMMIT
    // ═══════════════════════════════════════════════════════════════
    public async Task<BuyResult> BuyPessimisticAsync(int productId, string threadId)
    {
        const string method = "PESSIMISTIC_LOCK";
        var sw = Stopwatch.StartNew();

        await using var masterCon = new SqlConnection(MasterConnStr);
        await masterCon.OpenAsync();
        await using var tx = await masterCon.BeginTransactionAsync() as SqlTransaction;

        try
        {
            // ── BƯỚC 1: Đọc + Khóa row ───────────────────────────
            const string selectSql = @"
                SELECT ProductId, ProductName, Stock, SalePrice, Version
                FROM   Products WITH (UPDLOCK, ROWLOCK)
                WHERE  ProductId = @ProductId";

            var product = await masterCon.QueryFirstOrDefaultAsync<dynamic>(
                selectSql, new { ProductId = productId }, transaction: tx);

            if (product is null)
            {
                await tx!.RollbackAsync();
                return Result(threadId, method, false, "Sản phẩm không tồn tại.", sw);
            }

            int stockBefore = (int)product.Stock;

            if (stockBefore <= 0)
            {
                // Log FAILED
                await masterCon.ExecuteAsync(
                    @"INSERT INTO PurchaseLog (ProductId, ThreadId, Method, Action, StockBefore, StockAfter, Duration_Ms, Message, CreatedAt)
                      VALUES (@ProductId, @ThreadId, @Method, 'FAILED', @StockBefore, @StockBefore, @Duration, N'Hết hàng', SYSUTCDATETIME())",
                    new { ProductId = productId, ThreadId = threadId, Method = method, StockBefore = stockBefore, Duration = ElapsedMs(sw) },
                    transaction: tx);

                await tx!.CommitAsync();
                return Result(threadId, method, false, "Hết hàng.", sw);
            }

            // ── BƯỚC 2: Update Stock ──────────────────────────────
            await masterCon.ExecuteAsync(
                @"UPDATE Products SET Stock = Stock - 1, UpdatedAt = SYSUTCDATETIME()
                  WHERE ProductId = @ProductId AND Stock > 0",
                new { ProductId = productId }, transaction: tx);

            int stockAfter = await masterCon.ExecuteScalarAsync<int>(
                "SELECT Stock FROM Products WHERE ProductId = @ProductId",
                new { ProductId = productId }, transaction: tx);

            // ── BƯỚC 3: Insert Order ──────────────────────────────
            await masterCon.ExecuteAsync(
                @"INSERT INTO Orders (ProductId, ThreadId, Quantity, UnitPrice, Status, Method, CreatedAt)
                  VALUES (@ProductId, @ThreadId, 1, @UnitPrice, 'SUCCESS', @Method, SYSUTCDATETIME())",
                new { ProductId = productId, ThreadId = threadId, UnitPrice = (decimal)product.SalePrice, Method = method },
                transaction: tx);

            // ── BƯỚC 4: Insert PurchaseLog — SUCCESS ──────────────
            await masterCon.ExecuteAsync(
                @"INSERT INTO PurchaseLog (ProductId, ThreadId, Method, Action, StockBefore, StockAfter, Duration_Ms, Message, CreatedAt)
                  VALUES (@ProductId, @ThreadId, @Method, 'SUCCESS', @StockBefore, @StockAfter, @Duration, N'Mua thành công (Pessimistic)', SYSUTCDATETIME())",
                new { ProductId = productId, ThreadId = threadId, Method = method, StockBefore = stockBefore, StockAfter = stockAfter, Duration = ElapsedMs(sw) },
                transaction: tx);

            await tx!.CommitAsync();

            _logger.LogInformation("[PESSIMISTIC] ✅ {ThreadId} mua thành công.", threadId);
            return Result(threadId, method, true, "Thành công (Pessimistic Lock).", sw);
        }
        catch (Exception ex)
        {
            await tx!.RollbackAsync();
            _logger.LogError(ex, "[PESSIMISTIC] Lỗi.");
            return Result(threadId, method, false, $"Lỗi hệ thống: {ex.Message}", sw);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 4. BUY OPTIMISTIC LOCK
    //    EF Core + [ConcurrencyCheck] trên Version (INT)
    //    Đọc → Stock-- → Version++ → SaveChanges()
    //    Nếu conflict → DbUpdateConcurrencyException → FAILED (không retry)
    // ═══════════════════════════════════════════════════════════════
    public async Task<BuyResult> BuyOptimisticAsync(int productId, string threadId)
    {
        const string method = "OPTIMISTIC_LOCK";
        var sw = Stopwatch.StartNew();

        try
        {
            // Detach entity cũ nếu đang tracked (tránh lỗi khi nhiều request trên cùng scope)
            var tracked = _masterCtx.ChangeTracker
                .Entries<Product>()
                .FirstOrDefault(e => e.Entity.ProductId == productId);
            if (tracked is not null)
                tracked.State = EntityState.Detached;

            // ── ĐỌC từ MASTER (có tracking để EF dùng ConcurrencyCheck) ──
            var product = await _masterCtx.Products
                .FirstOrDefaultAsync(p => p.ProductId == productId);

            if (product is null)
                return Result(threadId, method, false, "Sản phẩm không tồn tại.", sw);

            int stockBefore = product.Stock;

            if (product.Stock <= 0)
            {
                // Ghi log FAILED qua Dapper (ngoài EF context)
                await LogPurchaseAsync(productId, threadId, method, "FAILED", stockBefore, stockBefore, ElapsedMs(sw), "Hết hàng");
                return Result(threadId, method, false, "Hết hàng.", sw);
            }

            // ── Trừ Stock + tăng Version trong memory ─────────────
            product.Stock -= 1;
            product.Version += 1;
            product.UpdatedAt = DateTime.UtcNow;

            // Tạo Order
            var order = new Order
            {
                ProductId = productId,
                ThreadId = threadId,
                Quantity = 1,
                UnitPrice = product.SalePrice,
                Status = "SUCCESS",
                Method = method
            };
            _masterCtx.Orders.Add(order);

            // Tạo PurchaseLog
            var log = new PurchaseLog
            {
                ProductId = productId,
                ThreadId = threadId,
                Method = method,
                Action = "SUCCESS",
                StockBefore = stockBefore,
                StockAfter = product.Stock,
                Duration_Ms = ElapsedMs(sw),
                Message = "Mua thành công (Optimistic)"
            };
            _masterCtx.PurchaseLogs.Add(log);

            // ── SaveChanges — EF thêm AND Version = @old vào WHERE ──
            await _masterCtx.SaveChangesAsync();

            _logger.LogInformation("[OPTIMISTIC] ✅ {ThreadId} mua thành công.", threadId);
            return Result(threadId, method, true, "Thành công (Optimistic Lock).", sw);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Conflict — thread khác đã thay đổi Version → FAILED (không retry)
            _logger.LogWarning("[OPTIMISTIC] ⚡ {ThreadId} bị conflict — FAILED.", threadId);

            // Ghi log FAILED qua Dapper (EF context bị lỗi, không dùng SaveChanges nữa)
            await LogPurchaseAsync(productId, threadId, method, "FAILED", null, null, ElapsedMs(sw), "Conflict — Version không khớp");

            return Result(threadId, method, false, "Thất bại — Conflict (Version không khớp).", sw);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OPTIMISTIC] Lỗi.");
            return Result(threadId, method, false, $"Lỗi hệ thống: {ex.Message}", sw);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 5. BUY SERIALIZABLE
    //    Dapper + Transaction mức SERIALIZABLE trên MASTER
    //    Toàn bộ SELECT-UPDATE-INSERT chạy ở isolation level cao nhất
    // ═══════════════════════════════════════════════════════════════
    public async Task<BuyResult> BuySerializableAsync(int productId, string threadId)
    {
        const string method = "SERIALIZABLE";
        var sw = Stopwatch.StartNew();

        await using var masterCon = new SqlConnection(MasterConnStr);
        await masterCon.OpenAsync();
        await using var tx = masterCon.BeginTransaction(IsolationLevel.Serializable);

        try
        {
            // ── BƯỚC 1: Đọc Stock trong transaction Serializable ──
            var product = await masterCon.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT ProductId, Stock, SalePrice FROM Products WHERE ProductId = @ProductId",
                new { ProductId = productId }, transaction: tx);

            if (product is null)
            {
                tx.Rollback();
                return Result(threadId, method, false, "Sản phẩm không tồn tại.", sw);
            }

            int stockBefore = (int)product.Stock;

            if (stockBefore <= 0)
            {
                await masterCon.ExecuteAsync(
                    @"INSERT INTO PurchaseLog (ProductId, ThreadId, Method, Action, StockBefore, StockAfter, Duration_Ms, Message, CreatedAt)
                      VALUES (@ProductId, @ThreadId, @Method, 'FAILED', @StockBefore, @StockBefore, @Duration, N'Hết hàng', SYSUTCDATETIME())",
                    new { ProductId = productId, ThreadId = threadId, Method = method, StockBefore = stockBefore, Duration = ElapsedMs(sw) },
                    transaction: tx);

                tx.Commit();
                return Result(threadId, method, false, "Hết hàng.", sw);
            }

            // ── BƯỚC 2: Update Stock ──────────────────────────────
            int rows = await masterCon.ExecuteAsync(
                @"UPDATE Products SET Stock = Stock - 1, UpdatedAt = SYSUTCDATETIME()
                  WHERE ProductId = @ProductId AND Stock > 0",
                new { ProductId = productId }, transaction: tx);

            if (rows == 0)
            {
                await masterCon.ExecuteAsync(
                    @"INSERT INTO PurchaseLog (ProductId, ThreadId, Method, Action, StockBefore, StockAfter, Duration_Ms, Message, CreatedAt)
                      VALUES (@ProductId, @ThreadId, @Method, 'FAILED', @StockBefore, @StockBefore, @Duration, N'Hết hàng (update failed)', SYSUTCDATETIME())",
                    new { ProductId = productId, ThreadId = threadId, Method = method, StockBefore = stockBefore, Duration = ElapsedMs(sw) },
                    transaction: tx);

                tx.Commit();
                return Result(threadId, method, false, "Hết hàng (update failed).", sw);
            }

            int stockAfter = await masterCon.ExecuteScalarAsync<int>(
                "SELECT Stock FROM Products WHERE ProductId = @ProductId",
                new { ProductId = productId }, transaction: tx);

            // ── BƯỚC 3: Insert Order ──────────────────────────────
            await masterCon.ExecuteAsync(
                @"INSERT INTO Orders (ProductId, ThreadId, Quantity, UnitPrice, Status, Method, CreatedAt)
                  VALUES (@ProductId, @ThreadId, 1, @UnitPrice, 'SUCCESS', @Method, SYSUTCDATETIME())",
                new { ProductId = productId, ThreadId = threadId, UnitPrice = (decimal)product.SalePrice, Method = method },
                transaction: tx);

            // ── BƯỚC 4: Insert PurchaseLog — SUCCESS ──────────────
            await masterCon.ExecuteAsync(
                @"INSERT INTO PurchaseLog (ProductId, ThreadId, Method, Action, StockBefore, StockAfter, Duration_Ms, Message, CreatedAt)
                  VALUES (@ProductId, @ThreadId, @Method, 'SUCCESS', @StockBefore, @StockAfter, @Duration, N'Mua thành công (Serializable)', SYSUTCDATETIME())",
                new { ProductId = productId, ThreadId = threadId, Method = method, StockBefore = stockBefore, StockAfter = stockAfter, Duration = ElapsedMs(sw) },
                transaction: tx);

            tx.Commit();

            _logger.LogInformation("[SERIALIZABLE] ✅ {ThreadId} mua thành công.", threadId);
            return Result(threadId, method, true, "Thành công (Serializable).", sw);
        }
        catch (Exception ex)
        {
            tx.Rollback();
            _logger.LogError(ex, "[SERIALIZABLE] Lỗi.");
            return Result(threadId, method, false, $"Lỗi hệ thống: {ex.Message}", sw);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Tạo BuyResult, tự đo Ticks từ Stopwatch.
    /// </summary>
    private static BuyResult Result(string threadId, string method, bool success, string message, Stopwatch sw)
    {
        sw.Stop();
        return new BuyResult(threadId, method, success, message, sw.ElapsedTicks);
    }

    /// <summary>
    /// Tính thời gian (ms) hiện tại của Stopwatch.
    /// </summary>
    private static decimal ElapsedMs(Stopwatch sw) =>
        (decimal)Convert.ToDecimal(sw.Elapsed.TotalMilliseconds);

    /// <summary>
    /// Ghi PurchaseLog bằng Dapper (dùng khi EF context bị lỗi / không khả dụng).
    /// </summary>
    private async Task LogPurchaseAsync(
        int productId, string threadId, string method, string action,
        int? stockBefore, int? stockAfter, decimal durationMs, string message)
    {
        try
        {
            await using var con = new SqlConnection(MasterConnStr);
            await con.OpenAsync();
            await con.ExecuteAsync(
                @"INSERT INTO PurchaseLog (ProductId, ThreadId, Method, Action, StockBefore, StockAfter, Duration_Ms, Message, CreatedAt)
                  VALUES (@ProductId, @ThreadId, @Method, @Action, @StockBefore, @StockAfter, @Duration, @Message, SYSUTCDATETIME())",
                new { ProductId = productId, ThreadId = threadId, Method = method, Action = action, StockBefore = stockBefore, StockAfter = stockAfter, Duration = durationMs, Message = message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Không thể ghi PurchaseLog.");
        }
    }
}