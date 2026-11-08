using SagaFlow.Core.Abstractions;
using SagaFlow.Core.State;
using SagaFlow.Engine.Coordination;
using SagaFlow.Core.Messages;

namespace SagaFlow.Procurement;

public sealed class ProcurementService(PurchaseRepository records, ISagaFlow flow, ISagaStateStore state, TimeProvider time)
{
    public async Task<PurchaseCreated> CreateAsync(CreatePurchaseRequest request, CancellationToken ct)
    {
        if (!request.IsValid()) throw new ArgumentException("Invalid purchase request.");
        var payload = new PurchasePayload(Guid.NewGuid(), request.EmployeeId.Trim(), request.CustomerId.Trim(),
            Array.AsReadOnly(request.Items.Select(i => i with { ProductId = i.ProductId.Trim() }).ToArray()), request.FailureAt);
        records.Add(new(payload, time.GetUtcNow()));
        try
        {
            var sagaId = await flow.StartAsync(payload, ct);
            records.Attach(payload.Id, sagaId);
            return new(payload.Id, sagaId);
        }
        catch (Exception error)
        {
            var sagaId = error is SagaDispatchException dispatch ? Guid.Parse(dispatch.InstanceId) : (Guid?)null;
            records.Attach(payload.Id, sagaId, true);
            throw new PurchaseStartException(payload.Id, error);
        }
    }
    public async Task<PurchaseView?> FindAsync(Guid id, CancellationToken ct)
    {
        var record = records.Find(id); if (record is null) return null;
        var saga = record.SagaInstanceId is { } sagaId ? await state.FindAsync(sagaId, ct) : null;
        var approval = saga?.Steps.SingleOrDefault(s => s.StepId == "managerApproval")?.Result;
        var rejected = approval is { } result && result.TryGetProperty("approved", out var approved) && approved.ValueKind == System.Text.Json.JsonValueKind.False;
        var invocation = saga?.Steps.SingleOrDefault(s => s.StepId == "invokeOrder");
        var status = saga?.Status switch
        {
            SagaStatus.Completed when rejected => "Rejected",
            SagaStatus.Completed => "Fulfilled",
            SagaStatus.Failed or SagaStatus.Compensated or SagaStatus.TimedOut => "Failed",
            _ when record.StartFailed => "Failed",
            _ when saga?.CurrentStep == "managerApproval" => "AwaitingApproval",
            _ when saga?.CurrentStep == "invokeOrder" => "Ordering",
            _ => "Pending"
        };
        return new(id, record.SagaInstanceId, record.Request.EmployeeId, status, saga?.Status.ToString(), saga?.CorrelationId,
            invocation?.Invocation?.Identity.InvocationId, invocation?.InvocationProgress?.TargetSagaInstanceId, invocation?.InvocationProgress?.Status.ToString());
    }
}

public sealed class ProcurementOrderStep(ISagaFlow flow) : ILocalSagaStepHandler
{
    public async Task ExecuteAsync(SagaCommand command, CancellationToken ct)
    {
        if (command.Name != "InvokeOrder") return;
        var payload = command.Payload;
        // Only business data is supplied. SagaFlow supplies invocation/source/step/correlation metadata.
        await flow.InvokeAsync("order-process", new { purchaseRequestId = payload.GetProperty("id").GetGuid(),
            order = new { customerId = payload.GetProperty("customerId").GetString(), items = payload.GetProperty("items"),
                failureAt = payload.GetProperty("failureAt") } }, ct);
    }
}
