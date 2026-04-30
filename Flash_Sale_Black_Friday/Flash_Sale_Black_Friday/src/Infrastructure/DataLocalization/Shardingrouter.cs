using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Flash_Sale_Black_Friday.src.Infrastructure.DataLocalization
{
    /// <summary>
    /// Hiện thực hóa C4 - Horizontal Fragmentation:
    ///
    ///     fragment_predicate(Orders)     = ProductId % 2
    ///     fragment_predicate(PurchaseLog)= ProductId % 2
    ///
    ///         ProductId chẵn → Node_Even  (DB FlashSale_Order_Even)
    ///         ProductId lẻ   → Node_Odd   (DB FlashSale_Order_Odd)
    ///
    /// Các fragment này DISJOINT và COMPLETE — tuân đúng 3 điều kiện
    /// đúng đắn của phân mảnh trong slide C4: Completeness, Reconstruction,
    /// Disjointness.
    /// </summary>
    public class ShardingRouter : IShardingRouter
    {
        private readonly string _master;
        private readonly string _nodeEven;
        private readonly string _nodeOdd;

        // Cache danh sách shard — Router là Singleton nên khởi tạo 1 lần.
        private readonly IReadOnlyList<(string NodeName, string ConnectionString)> _allShards;

        public ShardingRouter(IConfiguration config)
        {
            _master = config.GetConnectionString("Master")
                        ?? throw new InvalidOperationException("Missing Master connection string.");
            _nodeEven = config.GetConnectionString("Node_Even")
                        ?? throw new InvalidOperationException("Missing Node_Even connection string.");
            _nodeOdd = config.GetConnectionString("Node_Odd")
                        ?? throw new InvalidOperationException("Missing Node_Odd connection string.");

            _allShards = new List<(string, string)>
            {
                ("Node_Even", _nodeEven),
                ("Node_Odd",  _nodeOdd),
            }.AsReadOnly();
        }

        // -------- Fragmentation predicate: ProductId % 2 --------
        private string ResolveConn(int productId) =>
            (productId % 2 == 0) ? _nodeEven : _nodeOdd;

        public string ResolveShardName(int productId) =>
            (productId % 2 == 0) ? "Node_Even" : "Node_Odd";

        public SqlConnection OpenShardConnection(int productId)
        {
            var conn = new SqlConnection(ResolveConn(productId));
            conn.Open();
            return conn;
        }

        public SqlConnection OpenMasterConnection()
        {
            var conn = new SqlConnection(_master);
            conn.Open();
            return conn;
        }

        public IReadOnlyList<(string NodeName, string ConnectionString)> AllShards => _allShards;
    }
}