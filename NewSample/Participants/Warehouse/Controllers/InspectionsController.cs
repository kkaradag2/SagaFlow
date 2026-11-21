using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Sample.Participants.Warehouse.Domain;
using Sample.Participants.Warehouse.Services;

namespace Sample.Participants.Warehouse.Controllers;

[ApiController]
[Route("inspections")]
public sealed class InspectionsController(IInspectionService inspections) : ControllerBase
{
    [HttpGet("pending")]
    public async Task<ActionResult<IReadOnlyList<InspectionRecord>>> Pending(CancellationToken cancellationToken)
        => Ok(await inspections.GetPendingAsync(cancellationToken));

    [HttpGet("{returnId:guid}")]
    public async Task<ActionResult<InspectionRecord>> Get(Guid returnId, CancellationToken cancellationToken)
    {
        var inspection = await inspections.FindAsync(returnId, cancellationToken);
        return inspection is null ? NotFound() : Ok(inspection);
    }

    [HttpPost("{returnId:guid}/decision")]
    [ProducesResponseType<InspectionRecord>(StatusCodes.Status200OK)]
    [ProducesResponseType<InspectionRecord>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<InspectionRecord>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<InspectionRecord>> Decide(
        Guid returnId, InspectionDecisionRequest request, CancellationToken cancellationToken)
    {
        if (request.Decision is not ("accept" or "reject"))
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Decision must be accept or reject.");

        var accepted = request.Decision == "accept";
        InspectionRecord? record;
        try
        {
            record = await inspections.DecideAsync(returnId, accepted, cancellationToken);
        }
        catch (NpgsqlException)
        {
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Inspection state could not be confirmed.");
        }

        if (record is null)
            return NotFound();
        if (record.Accepted != accepted)
            return Conflict(record);

        return record.PublishedAt is null
            ? AcceptedAtAction(nameof(Get), new { returnId }, record)
            : Ok(record);
    }
}

public sealed record InspectionDecisionRequest(string? Decision);
