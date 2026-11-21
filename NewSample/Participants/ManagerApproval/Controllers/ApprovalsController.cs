using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Sample.Participants.ManagerApproval.Domain;
using Sample.Participants.ManagerApproval.Services;

namespace Sample.Participants.ManagerApproval.Controllers;

[ApiController]
[Route("approvals")]
public sealed class ApprovalsController(IApprovalService approvals) : ControllerBase
{
    [HttpGet("pending")]
    public async Task<ActionResult<IReadOnlyList<ApprovalRecord>>> Pending(CancellationToken cancellationToken)
        => Ok(await approvals.GetPendingAsync(cancellationToken));

    [HttpGet("{approvalId:guid}")]
    public async Task<ActionResult<ApprovalRecord>> Get(Guid approvalId, CancellationToken cancellationToken)
    {
        var approval = await approvals.FindAsync(approvalId, cancellationToken);
        return approval is null ? NotFound() : Ok(approval);
    }

    [HttpPost("{approvalId:guid}/decision")]
    [ProducesResponseType<ApprovalRecord>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApprovalRecord>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApprovalRecord>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ApprovalRecord>> Decide(
        Guid approvalId, ApprovalDecisionRequest request, CancellationToken cancellationToken)
    {
        if (request.Decision is not ("approve" or "reject"))
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Decision must be approve or reject.");

        var approved = request.Decision == "approve";
        ApprovalRecord? record;
        try
        {
            record = await approvals.DecideAsync(approvalId, approved, cancellationToken);
        }
        catch (NpgsqlException)
        {
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Approval state could not be confirmed.");
        }

        if (record is null)
            return NotFound();
        if (record.Approved != approved)
            return Conflict(record);

        return record.PublishedAt is null
            ? AcceptedAtAction(nameof(Get), new { approvalId }, record)
            : Ok(record);
    }
}

public sealed record ApprovalDecisionRequest(string? Decision);
