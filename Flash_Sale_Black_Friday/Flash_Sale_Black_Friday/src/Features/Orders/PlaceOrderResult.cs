namespace Features.Orders;

public class PlaceOrderResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int? OrderId { get; set; }
}