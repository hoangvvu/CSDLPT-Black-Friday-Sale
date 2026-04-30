namespace Infrastructure.DataLocalization;

public class NodeConfig
{
    // Phải khớp đúng với key trong appsettings.json → ConnectionStrings
    public string Node1Master { get; set; } = string.Empty;
    public string Node1Slave { get; set; } = string.Empty;
    public string Node2 { get; set; } = string.Empty;
}