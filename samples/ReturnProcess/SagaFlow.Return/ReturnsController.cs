using Microsoft.AspNetCore.Mvc;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.State;

namespace SagaFlow.Returns;

[ApiController]
[Route("returns")]
public sealed class ReturnsController(ReturnRepository repository, ISagaFlow sagaFlow, ISagaStateStore state) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(CreateReturnRequest request, CancellationToken cancellationToken)
    {
        if (request.OrderId == Guid.Empty || string.IsNullOrWhiteSpace(request.CustomerId) || request.Amount <= 0 ||
            request.Items is not { Count: > 0 } ||
            request.Items.Any(x => string.IsNullOrWhiteSpace(x.ProductId) || x.Quantity <= 0))
            return BadRequest(new { error = "A valid order, customer, amount and at least one item are required." });
        var returnId = Guid.NewGuid();
        var payload = new ReturnPayload(returnId, request.OrderId, request.CustomerId.Trim(), request.Amount,
            request.Items.Select(x => new ReturnItem(x.ProductId.Trim(), x.Quantity)).ToArray(), request.FailureAt);
        var sagaId = await sagaFlow.StartAsync(payload, cancellationToken);
        repository.Add(new ReturnCase(returnId, payload.OrderId, payload.CustomerId, payload.Amount,
            payload.Items, payload.FailureAt, DateTimeOffset.UtcNow, sagaId));
        return Created($"/returns/{returnId}", new ReturnCreated(returnId, request.OrderId, sagaId));
    }

    [HttpGet("{returnId:guid}")]
    public async Task<IActionResult> Get(Guid returnId, CancellationToken cancellationToken)
    {
        var record = repository.Find(returnId);
        if (record is null) return NotFound();
        var saga = await state.FindAsync(record.SagaInstanceId, cancellationToken);
        var rejected = saga?.Steps.SingleOrDefault(x => x.StepId == "rejectedNotification")?.Status == SagaStepStatus.Completed;
        var refundCompleted = saga?.Steps.SingleOrDefault(x => x.StepId == "payment")?.Status == SagaStepStatus.Completed;
        var status = saga?.Status switch
        {
            SagaStatus.Completed when rejected => "Rejected",
            SagaStatus.Completed => "Accepted",
            SagaStatus.Failed or SagaStatus.Compensated when refundCompleted => "ManualReview",
            SagaStatus.Failed or SagaStatus.Compensated => "Failed",
            _ => "Pending"
        };
        return Ok(new { record.ReturnId, record.OrderId, record.SagaInstanceId, record.CustomerId,
            BusinessStatus = status, SagaStatus = saga?.Status.ToString(),
            SagaCorrelationId = saga?.CorrelationId });
    }
}

public sealed record ReturnPayload(Guid ReturnId, Guid OrderId, string CustomerId, decimal Amount,
    IReadOnlyList<ReturnItem> Items, string? FailureAt);
