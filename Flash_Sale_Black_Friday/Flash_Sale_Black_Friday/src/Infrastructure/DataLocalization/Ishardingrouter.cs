using Microsoft.Data.SqlClient;

namespace Flash_Sale_Black_Friday.src.Infrastructure.DataLocalization
{
    public interface IShardingRouter
    {
        /// <summary>Mở connection tới shard chứa ProductId (cho Orders/Log).</summary>
        SqlConnection OpenShardConnection(int productId);

        /// <summary>Mở connection tới Master (chứa bảng Products - không phân mảnh).</summary>
        SqlConnection OpenMasterConnection();

        /// <summary>Tên node tương ứng ProductId (dùng cho log/demo).</summary>
        string ResolveShardName(int productId);

        /// <summary>Toàn bộ connection string của các shard (dùng cho global query).</summary>
        IReadOnlyList<(string NodeName, string ConnectionString)> AllShards { get; }
    }
}
