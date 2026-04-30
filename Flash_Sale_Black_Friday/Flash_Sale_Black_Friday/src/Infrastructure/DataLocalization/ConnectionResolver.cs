using Microsoft.Extensions.Configuration;

namespace Infrastructure.DataLocalization; // ← thêm namespace

public class ConnectionResolver : IConnectionResolver
{
    private readonly IConfiguration _config;

    private static readonly Dictionary<string, (NodeRoute Master, NodeRoute Slave)> _routingTable
        = new(StringComparer.OrdinalIgnoreCase)
        {
            ["nike"] = (NodeRoute.Node1Master, NodeRoute.Node1Slave),
            ["adidas"] = (NodeRoute.Node2, NodeRoute.Node2),
        };

    public ConnectionResolver(IConfiguration config)
    {
        _config = config;
    }

    public string Resolve(string brand, bool readOnly = false)
    {
        if (!_routingTable.TryGetValue(brand, out var routes))
            throw new NotSupportedException($"Brand '{brand}' chưa được cấu hình.");

        var targetRoute = readOnly ? routes.Slave : routes.Master;
        return Resolve(targetRoute);
    }

    public string Resolve(NodeRoute route)
    {
        var key = route.ToString(); // "Node1Master" | "Node1Slave" | "Node2"
        return _config.GetConnectionString(key)
            ?? throw new InvalidOperationException(
                $"Connection string '{key}' không tồn tại trong appsettings.json");
    }
}