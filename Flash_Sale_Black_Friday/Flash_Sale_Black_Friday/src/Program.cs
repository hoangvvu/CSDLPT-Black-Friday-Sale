using Flash_Sale_Black_Friday.src.Infrastructure.DataLocalization;
using Flash_Sale_Black_Friday.src.Services;
using Infrastructure.Persistence;   // MasterDbContext
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Services;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// ── 1. CORS — Cho phép HTML file:// / Live Server gọi API ─────────────────────
builder.Services.AddCors(opts =>
    opts.AddPolicy("AllowAll", p =>
        p.AllowAnyOrigin()
         .AllowAnyMethod()
         .AllowAnyHeader()));

builder.Services.AddDbContext<MasterDbContext>(opts =>
    opts.UseSqlServer(cfg.GetConnectionString("MasterDb")));

var redisConnection = builder.Configuration.GetConnectionString("Redis")
                      ?? "localhost:6379";
builder.Services.AddSingleton<IConnectionMultiplexer>(
    ConnectionMultiplexer.Connect(redisConnection));

// ── 4. Controllers + Swagger ──────────────────────────────────────────────────
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        // camelCase cho JSON response (khớp với frontend JS)
        o.JsonSerializerOptions.PropertyNamingPolicy =
            System.Text.Json.JsonNamingPolicy.CamelCase;
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Flash Sale API — Concurrency Demo",
        Version = "v1",
        Description = "Demo: No Lock · Atomic · Pessimistic · Optimistic · Serializable · Redis"
    }));
builder.Services.AddHostedService<RedisQueueWorker>();

builder.Services.AddSingleton<IShardingRouter, ShardingRouter>();
// Repository stateless — scoped cũng được, singleton cũng ok.
builder.Services.AddSingleton<IDistributedOrderRepository, DistributedOrderRepository>();

// ── 5. Build & Configure Pipeline ────────────────────────────────────────────
var app = builder.Build();

app.UseCors("AllowAll");   // ← Bắt buộc phải trước MapControllers

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Flash Sale API v1");
        c.RoutePrefix = "swagger";
    });
}

app.MapControllers();
app.Run();