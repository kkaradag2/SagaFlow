using Microsoft.AspNetCore.Mvc;

namespace SagaFlow.Warehouse;

public sealed record InspectionDecisionRequest(string? Decision);

[ApiController]
[Route("inspections")]
public sealed class InspectionsController(WarehouseRepository repository, WarehouseDecisionService decisions) : ControllerBase
{
    [HttpGet("pending")]
    public IActionResult Pending() => Ok(repository.Pending());

    [HttpGet("{returnId:guid}")]
    public IActionResult Get(Guid returnId) => repository.Find(returnId) is { } record ? Ok(record) : NotFound();

    [HttpPost("{returnId:guid}/decision")]
    public async Task<IActionResult> Decide(Guid returnId, InspectionDecisionRequest request, CancellationToken cancellationToken)
    {
        if (request.Decision is not ("accept" or "reject"))
            return BadRequest(new { error = "Decision must be accept or reject." });
        try
        {
            var result = await decisions.DecideAsync(returnId, request.Decision == "accept", cancellationToken);
            return result.Published ? Ok(result.Inspection) : StatusCode(503, result.Inspection);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException error) { return Conflict(new { error = error.Message }); }
    }
}
