using Microsoft.AspNetCore.Mvc;
using SagaFlow.Order.Contracts.Responses;
using SagaFlow.Order.Domain;
using SagaFlow.Order.Services;

namespace SagaFlow.Order.Controllers;

[ApiController]
[Route("orders")]
public sealed class OrdersController(IOrderService orders) : ControllerBase
{
    [HttpGet("invocations/{invocationId}")]
    public async Task<IActionResult> FindIncoming(string invocationId, [FromServices] IncomingOrderService incoming, CancellationToken cancellationToken)
    {
        var link = await incoming.FindAsync(invocationId, cancellationToken);
        return link is null ? NotFound() : Ok(link);
    }
    [HttpPost]
    [ProducesResponseType<SuccessResponse<CreateOrderResult>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(CreateOrderRequest request, CancellationToken cancellationToken)
    {
        var result = await orders.CreateAsync(request, cancellationToken);
        return Created($"/orders/{result.OrderId}", new SuccessResponse<CreateOrderResult>(result));
    }
}
