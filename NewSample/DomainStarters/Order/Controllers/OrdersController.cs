using Microsoft.AspNetCore.Mvc;
using Sample.DomainStarters.Order.Contracts;
using Sample.DomainStarters.Order.Domain;
using Sample.DomainStarters.Order.Services;

namespace Sample.DomainStarters.Order.Controllers;

[ApiController]
[Route("orders")]
public sealed class OrdersController(IOrderService orders) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<OrderDetails>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<OrderDetails>> Create(
        CreateOrderRequest request, CancellationToken cancellationToken)
    {
        var order = await orders.CreateAsync(request, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, order);
    }
}
