namespace Services;

/// <summary>
/// Kết quả trả về từ mỗi hàm mua hàng.
/// </summary>
public record BuyResult(
    string ThreadId,
    string Method,
    bool Success,
    string Message,
    long Ticks
);

public interface IFlashSaleService
{
    /// <summary>
    /// Kịch bản 1 — NO LOCK: Đọc từ Slave → Delay 100ms → Ghi lên Master (Dapper).
    /// Demo hậu quả Replication Lag + không có cơ chế lock → Oversell.
    /// </summary>
    Task<BuyResult> BuyNoLockAsync(int productId, string threadId);

    /// <summary>
    /// Kịch bản 2 — ATOMIC: Gọi SP dbo.sp_Purchase_Atomic trên Master (Dapper).
    /// UPDATE ... WHERE Stock > 0 trong 1 câu lệnh nguyên tử.
    /// </summary>
    Task<BuyResult> BuyAtomicAsync(int productId, string threadId);

    /// <summary>
    /// Kịch bản 3 — PESSIMISTIC LOCK: Dapper + SELECT WITH (UPDLOCK) trên Master.
    /// Row bị khóa ngay khi đọc → thread khác phải chờ.
    /// </summary>
    Task<BuyResult> BuyPessimisticAsync(int productId, string threadId);

    /// <summary>
    /// Kịch bản 4 — OPTIMISTIC LOCK: EF Core + ConcurrencyCheck trên Version (INT).
    /// Bắt DbUpdateConcurrencyException → trả FAILED.
    /// </summary>
    Task<BuyResult> BuyOptimisticAsync(int productId, string threadId);

    /// <summary>
    /// Kịch bản 5 — SERIALIZABLE: Dapper + Transaction mức Serializable trên Master.
    /// </summary>
    Task<BuyResult> BuySerializableAsync(int productId, string threadId);
}