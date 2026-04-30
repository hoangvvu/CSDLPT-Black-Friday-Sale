using Dapper;
using Infrastructure.DataLocalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.Threading.Tasks;

namespace Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProductsController : ControllerBase
{
    private readonly INodeRouter _nodeRouter;

    public ProductsController(INodeRouter nodeRouter)
    {
        _nodeRouter = nodeRouter;
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetProduct(int id, [FromQuery] string brand)
    {
        if (string.IsNullOrEmpty(brand))
            return BadRequest("Vui lòng cung cấp Brand để định tuyến.");

        // Lấy dữ liệu trỏ về Slave (isReadOnly = true) để tối ưu truy vấn đọc
        var connStr = _nodeRouter.GetConnectionString(brand, isReadOnly: true);
        using var connection = new SqlConnection(connStr);

        var product = await connection.QuerySingleOrDefaultAsync(
            "SELECT * FROM Products WHERE Id = @Id", new { Id = id });

        if (product == null) return NotFound("Không tìm thấy dữ liệu ở Node này.");

        return Ok(product);
    }
}