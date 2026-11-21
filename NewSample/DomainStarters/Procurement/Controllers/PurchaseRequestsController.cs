using Microsoft.AspNetCore.Mvc;
using Sample.DomainStarters.Procurement.Contracts;
using Sample.DomainStarters.Procurement.Services;

namespace Sample.DomainStarters.Procurement.Controllers;

[ApiController]
[Route("purchase-requests")]
public sealed class PurchaseRequestsController(IProcurementService purchases) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<PurchaseCreated>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<PurchaseCreated>> Create(CreatePurchaseRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var created = await purchases.CreateAsync(request, cancellationToken);
            return CreatedAtAction(nameof(Get), new { id = created.PurchaseRequestId }, created);
        }
        catch (PurchaseStartException error)
        {
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: error.Message,
                instance: Url.Action(nameof(Get), new { id = error.PurchaseId }), extensions: new Dictionary<string, object?>
                {
                    ["purchaseRequestId"] = error.PurchaseId,
                    ["sagaInstanceId"] = error.SagaId
                });
        }
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<PurchaseStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PurchaseStatusResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        var view = await purchases.FindAsync(id, cancellationToken);
        return view is null ? NotFound() : Ok(view);
    }
}
