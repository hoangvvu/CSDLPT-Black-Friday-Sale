using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Models;

[Table("Products")]
public class Product
{
    [Key]
    public int ProductId { get; set; }

    [Required, MaxLength(200)]
    public string ProductName { get; set; } = string.Empty;

    [Column(TypeName = "decimal(18,2)")]
    public decimal OriginalPrice { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal SalePrice { get; set; }

    public int Stock { get; set; }

    /// <summary>
    /// Optimistic Concurrency Token — EF Core sẽ thêm AND Version = @old vào WHERE khi UPDATE.
    /// Kiểu INT, tự tăng bằng tay (Version++) trong code.
    /// </summary>
    [ConcurrencyCheck]
    public int Version { get; set; } = 1;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}