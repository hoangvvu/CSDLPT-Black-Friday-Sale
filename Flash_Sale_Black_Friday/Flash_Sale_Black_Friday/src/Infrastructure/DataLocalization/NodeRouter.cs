using Microsoft.Extensions.Configuration;

namespace Infrastructure.DataLocalization;

public class NodeRouter : INodeRouter
{
    private readonly IConfiguration _config;

    private static readonly Dictionary<string, string> _routingTable = new()
    {
        { "Nike",   "Node1" },
        { "Adidas", "Node2" }
    };

    // ✅ Inject IConfiguration thay vì IOptions<NodeConfig>
    public NodeRouter(IConfiguration config)
    {
        _config = config;
    }

    public string GetConnectionString(string brand, bool isReadOnly = false)
    {
        if (!_routingTable.TryGetValue(brand, out var nodeName))
            throw new Exception($"[Chương 4] Không tìm thấy Node cho brand: {brand}");

        // Đọc thẳng từ appsettings.json → ConnectionStrings
        string key = nodeName switch
        {
            "Node1" => isReadOnly ? "Node1Slave" : "Node1Master",
            "Node2" => "Node2",
            _ => throw new Exception($"Node không hợp lệ: {nodeName}")
        };

        return _config.GetConnectionString(key)
            ?? throw new Exception($"Connection string '{key}' không tồn tại trong appsettings.json");
    }
}