using Features.Orders;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/Orders")]
public class OrdersController : ControllerBase
{
    private readonly PlaceOrderHandler _handler;

    public OrdersController(PlaceOrderHandler handler)
        => _handler = handler;

    [HttpPost("flash-sale/optimistic")]
    public async Task<IActionResult> PlaceOrder([FromBody] PlaceOrderCommand command)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var result = await _handler.HandleAsync(command);
        return result.Success ? Ok(result) : Conflict(result);
    }
}