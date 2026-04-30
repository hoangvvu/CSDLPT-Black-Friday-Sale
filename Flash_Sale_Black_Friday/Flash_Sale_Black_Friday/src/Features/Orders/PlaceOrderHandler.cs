using Dapper;
using Infrastructure.DataLocalization;
using Microsoft.Data.SqlClient;

namespace Features.Orders;

public class PlaceOrderHandler
{
    private readonly INodeRouter _nodeRouter;

    public PlaceOrderHandler(INodeRouter nodeRouter)
    {
        _nodeRouter = nodeRouter;
    }

    public async Task<PlaceOrderResult> HandleAsync(PlaceOrderCommand command)
    {
        // CHƯƠNG 4: DATA LOCALIZATION
        var connStr = _nodeRouter.GetConnectionString(command.Brand, isReadOnly: false);

        await using var connection = new SqlConnection(connStr);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync() as SqlTransaction;

        try
        {
            // CHƯƠNG 3: OPTIMISTIC LOCKING
            // Bước 1: Đọc Stock + Version
            // ✅ Tên cột đúng: ProductId (không phải Id)
            var product = await connection.QuerySingleOrDefaultAsync(
                @"SELECT ProductId, Stock, Version
                  FROM   Products
                  WHERE  ProductId = @ProductId",
                new { command.ProductId },
                transaction: tx);

            if (product == null)
                return new PlaceOrderResult { Success = false, Message = "Sản phẩm không tồn tại." };

            if (product.Stock < command.Quantity)
                return new PlaceOrderResult { Success = false, Message = "Hết hàng." };

            // Bước 2: UPDATE với Version lock
            // ✅ Không có "Version = Version + 1" — ROWVERSION tự tăng
            var rowsAffected = await connection.ExecuteAsync(
                @"UPDATE Products
                  SET    Stock = Stock - @Quantity
                  WHERE  ProductId = @ProductId
                    AND  Stock     >= @Quantity
                    AND  Version   = @CapturedVersion",
                new
                {
                    command.Quantity,
                    command.ProductId,
                    CapturedVersion = (byte[])product.Version
                },
                transaction: tx);

            if (rowsAffected == 0)
            {
                await tx!.RollbackAsync();
                return new PlaceOrderResult { Success = false, Message = "Conflict! Vui lòng thử lại." };
            }

            // Bước 3: Insert Order
            // ✅ Tên cột đúng: CreatedAt, OrderId
            var orderId = await connection.ExecuteScalarAsync<int>(
                @"INSERT INTO Orders (UserId, ProductId, Quantity, Status, CreatedAt)
                  OUTPUT INSERTED.OrderId
                  VALUES (@UserId, @ProductId, @Quantity, 'Success', GETDATE())",
                new { command.UserId, command.ProductId, command.Quantity },
                transaction: tx);

            await tx!.CommitAsync();

            return new PlaceOrderResult { Success = true, Message = "Chốt đơn thành công!", OrderId = orderId };
        }
        catch (Exception ex)
        {
            await tx!.RollbackAsync();
            return new PlaceOrderResult { Success = false, Message = $"Lỗi: {ex.Message}" };
        }
    }
}