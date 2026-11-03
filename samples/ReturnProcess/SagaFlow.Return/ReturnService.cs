using SagaFlow.Core.Abstractions;
using SagaFlow.Core.State;

namespace SagaFlow.Returns;

public sealed class ReturnService(ReturnRepository repository, ISagaFlow sagaFlow, ISagaStateStore state)
{
    public async Task<ReturnCreated> CreateAsync(CreateReturnRequest request, CancellationToken cancellationToken)
    {
        var returnId = Guid.NewGuid();
        var payload = new ReturnPayload(returnId, request.OrderId, request.CustomerId.Trim(), request.Amount,
            request.Items.Select(x => new ReturnItem(x.ProductId.Trim(), x.Quantity)).ToArray(), request.FailureAt);
        var sagaId = await sagaFlow.StartAsync(payload, cancellationToken);
        repository.Add(new ReturnCase(returnId, payload.OrderId, payload.CustomerId, payload.Amount,
            payload.Items, payload.FailureAt, DateTimeOffset.UtcNow, sagaId));
        return new ReturnCreated(returnId, request.OrderId, sagaId);
    }

    public async Task<ReturnView?> FindAsync(Guid returnId, CancellationToken cancellationToken)
    {
        var record = repository.Find(returnId);
        if (record is null) return null;
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
        return new ReturnView(record.ReturnId, record.OrderId, record.SagaInstanceId, record.CustomerId,
            status, saga?.Status.ToString(), saga?.CorrelationId);
    }
}
