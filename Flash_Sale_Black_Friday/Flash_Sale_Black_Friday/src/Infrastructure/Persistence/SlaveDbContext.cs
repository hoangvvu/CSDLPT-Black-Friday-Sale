using Models;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence;

/// <summary>
/// DbContext kết nối Node1-Slave — CHỈ DÙNG ĐỂ ĐỌC.
/// Dữ liệu có thể chậm hơn Master (Replication Lag).
/// </summary>
public class SlaveDbContext : DbContext
{
    public SlaveDbContext(DbContextOptions<SlaveDbContext> options)
        : base(options) { }

    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<PurchaseLog> PurchaseLogs => Set<PurchaseLog>();
}