namespace Infrastructure.DataLocalization;

public interface INodeRouter
{
    string GetConnectionString(string brand, bool isReadOnly = false);
}

public enum NodeRoute
{
    Node1Master,   // Nike  — nhận GHI
    Node1Slave,    // Nike  — nhận ĐỌC (có Replication Lag)
    Node2          // Adidas
}