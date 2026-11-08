using Microsoft.AspNetCore.Mvc;

namespace SagaFlow.Procurement.Controllers;

[ApiController]
[Route("purchase-requests")]
public sealed class PurchaseRequestsController(ProcurementService purchases) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(CreatePurchaseRequest request, CancellationToken ct)
    {
        if (!request.IsValid()) return BadRequest(new { error = "Employee, customer, items and supported failure target are required/valid." });
        try
        {
            var result = await purchases.CreateAsync(request, ct);
            return Created($"/purchase-requests/{result.PurchaseRequestId}", result);
        }
        catch (PurchaseStartException error)
        { return StatusCode(503, new { error.PurchaseRequestId, error = "Purchase request persisted; local orchestration could not start. Inspect its local state." }); }
    }
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Find(Guid id, CancellationToken ct)
    { var view = await purchases.FindAsync(id, ct); return view is null ? NotFound() : Ok(view); }
}
