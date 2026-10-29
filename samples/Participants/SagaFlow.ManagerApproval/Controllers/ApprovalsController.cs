using Microsoft.AspNetCore.Mvc;
using SagaFlow.ManagerApproval.Repositories;
using SagaFlow.ManagerApproval.Services;

namespace SagaFlow.ManagerApproval.Controllers;

public sealed record ApprovalDecisionRequest(string? Decision);

[ApiController]
[Route("approvals")]
public sealed class ApprovalsController(InMemoryApprovalRepository approvals, ApprovalDecisionService decisions) : ControllerBase
{
    [HttpGet("pending")]
    public IActionResult Pending() => Ok(approvals.Pending());

    [HttpGet("{orderId:guid}")]
    public IActionResult Get(Guid orderId)
    {
        var matches = approvals.Find(orderId);
        return matches.Count switch
        {
            0 => NotFound(),
            1 => Ok(matches[0]),
            _ => Conflict(new { error = "Multiple Saga approvals exist for this Order ID; the request is ambiguous." })
        };
    }

    [HttpPost("{orderId:guid}/decision")]
    public async Task<IActionResult> Decide(Guid orderId, ApprovalDecisionRequest request, CancellationToken cancellationToken)
    {
        if (request.Decision is not ("approve" or "reject"))
            return BadRequest(new { error = "Decision must be approve or reject." });
        try
        {
            var result = await decisions.DecideAsync(orderId, request.Decision == "approve", cancellationToken);
            return result.Published ? Ok(result.Approval) : StatusCode(StatusCodes.Status503ServiceUnavailable, result.Approval);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException error) { return Conflict(new { error = error.Message }); }
    }
}
