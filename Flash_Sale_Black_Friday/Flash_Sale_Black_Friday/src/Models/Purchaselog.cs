using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Models;

[Table("PurchaseLog")]
public class PurchaseLog
{
    [Key]
    public long LogId { get; set; }

    public int ProductId { get; set; }

    [Required, MaxLength(50)]
    public string ThreadId { get; set; } = string.Empty;

    [Required, MaxLength(50)]
    public string Method { get; set; } = string.Empty;

    [Required, MaxLength(20)]
    public string Action { get; set; } = string.Empty;    // ATTEMPT / SUCCESS / FAILED / ERROR

    public int? StockBefore { get; set; }
    public int? StockAfter { get; set; }

    [MaxLength(500)]
    public string? Message { get; set; }

    [Column(TypeName = "decimal(10,2)")]
    public decimal? Duration_Ms { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(ProductId))]
    public Product? Product { get; set; }
}