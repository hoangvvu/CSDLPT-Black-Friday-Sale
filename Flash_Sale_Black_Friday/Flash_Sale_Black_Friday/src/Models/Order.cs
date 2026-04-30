using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Models;

[Table("Orders")]
public class Order
{
    [Key]
    public int OrderId { get; set; }

    public int ProductId { get; set; }

    [Required, MaxLength(50)]
    public string ThreadId { get; set; } = string.Empty;

    public int Quantity { get; set; } = 1;

    [Column(TypeName = "decimal(18,2)")]
    public decimal UnitPrice { get; set; }

    [Required, MaxLength(20)]
    public string Status { get; set; } = "SUCCESS";

    [Required, MaxLength(50)]
    public string Method { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(ProductId))]
    public Product? Product { get; set; }
}