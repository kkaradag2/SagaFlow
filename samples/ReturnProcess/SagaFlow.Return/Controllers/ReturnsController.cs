using Microsoft.AspNetCore.Mvc;

namespace SagaFlow.Returns;

[ApiController]
[Route("returns")]
public sealed class ReturnsController(ReturnService returns) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(CreateReturnRequest request, CancellationToken cancellationToken)
    {
        if (request.OrderId == Guid.Empty || string.IsNullOrWhiteSpace(request.CustomerId) || request.Amount <= 0 ||
            request.Items is not { Count: > 0 } ||
            request.Items.Any(x => string.IsNullOrWhiteSpace(x.ProductId) || x.Quantity <= 0))
            return BadRequest(new { error = "A valid order, customer, amount and at least one item are required." });
        var created = await returns.CreateAsync(request, cancellationToken);
        return Created($"/returns/{created.ReturnId}", created);
    }

    [HttpGet("{returnId:guid}")]
    public async Task<IActionResult> Get(Guid returnId, CancellationToken cancellationToken)
    {
        var result = await returns.FindAsync(returnId, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }
}
