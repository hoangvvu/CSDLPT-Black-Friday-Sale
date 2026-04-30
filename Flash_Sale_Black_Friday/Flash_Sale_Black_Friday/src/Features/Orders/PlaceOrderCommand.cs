namespace Features.Orders;

public class PlaceOrderCommand
{
    public int UserId { get; set; }
    public int ProductId { get; set; }
    public string Brand { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
}