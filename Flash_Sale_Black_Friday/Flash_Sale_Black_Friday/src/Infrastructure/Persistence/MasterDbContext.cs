using Models;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence;

/// <summary>
/// DbContext kết nối Node1-Master — dùng cho mọi thao tác GHI.
/// </summary>
public class MasterDbContext : DbContext
{
    public MasterDbContext(DbContextOptions<MasterDbContext> options)
        : base(options) { }

    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<PurchaseLog> PurchaseLogs => Set<PurchaseLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(entity =>
        {
            // Version kiểu INT, dùng [ConcurrencyCheck] trên model.
            // EF Core sẽ thêm AND Version = @old vào WHERE khi UPDATE.
            entity.Property(p => p.Version)
                  .IsConcurrencyToken();

            entity.Property(p => p.Stock)
                  .HasDefaultValue(0);

            entity.Property(p => p.Version)
                  .HasDefaultValue(1);
        });

        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasIndex(o => o.Method).HasDatabaseName("IX_Orders_Method");
            entity.HasIndex(o => o.ProductId).HasDatabaseName("IX_Orders_ProductId");
        });

        modelBuilder.Entity<PurchaseLog>(entity =>
        {
            entity.HasIndex(l => l.Method).HasDatabaseName("IX_PurchaseLog_Method");
        });
    }
}