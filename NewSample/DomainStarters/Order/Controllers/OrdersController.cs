using Microsoft.AspNetCore.Mvc;
using Sample.DomainStarters.Order.Contracts;
using Sample.DomainStarters.Order.Services;

namespace Sample.DomainStarters.Order.Controllers;

[ApiController]
[Route("orders")]
public sealed class OrdersController(IOrderService orders) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<CreateOrderResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<CreateOrderResponse>> Create(
        CreateOrderRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var order = await orders.CreateAsync(request, cancellationToken);
            return StatusCode(StatusCodes.Status201Created, order);
        }
        catch (OrderSagaStartException error)
        {
            var problem = new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Order saved; Saga startup was not confirmed",
                Extensions = { ["orderId"] = error.OrderId }
            };
            if (error.SagaInstanceId is { } sagaInstanceId)
                problem.Extensions["sagaInstanceId"] = sagaInstanceId;
            return StatusCode(StatusCodes.Status503ServiceUnavailable, problem);
        }
    }
}
