using System.Text.Json;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.State;
using SagaFlow.Engine.Coordination;
using Sample.DomainStarters.Procurement.Contracts;
using Sample.DomainStarters.Procurement.Data;

namespace Sample.DomainStarters.Procurement.Services;

internal sealed class ProcurementService(
    PurchaseRepository repository, ISagaFlow flow, ISagaStateStore store, ILogger<ProcurementService> logger)
    : IProcurementService
{
    public async Task<PurchaseCreated> CreateAsync(CreatePurchaseRequest request, CancellationToken cancellationToken)
    {
        var purchase = new PurchasePayload(Guid.NewGuid(), request.EmployeeId.Trim(), request.CustomerId.Trim(),
            request.Items.Select(item => item with { ProductId = item.ProductId.Trim() }).ToArray(), request.FailStep?.Trim());
        await repository.AddAsync(purchase, cancellationToken);

        Guid sagaId;
        try
        {
            sagaId = await flow.StartAsync(purchase, CancellationToken.None);
        }
        catch (Exception error)
        {
            Guid? knownSagaId = error is SagaDispatchException dispatch &&
                Guid.TryParse(dispatch.InstanceId, out var instanceId) ? instanceId : null;
            await repository.RecordSagaStartAsync(purchase.Id, knownSagaId, unconfirmed: true);
            logger.LogError(error, "Saga startup could not be confirmed for purchase {PurchaseRequestId}", purchase.Id);
            throw new PurchaseStartException(purchase.Id, knownSagaId, error);
        }

        await repository.RecordSagaStartAsync(purchase.Id, sagaId, unconfirmed: false);
        logger.LogInformation("Purchase {PurchaseRequestId} started Saga {SagaInstanceId}", purchase.Id, sagaId);
        return new(purchase.Id, sagaId);
    }

    public async Task<PurchaseStatusResponse?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var purchase = await repository.FindAsync(id, cancellationToken);
        if (purchase is null)
            return null;

        var saga = purchase.SagaInstanceId is { } sagaId ? await store.FindAsync(sagaId, cancellationToken) : null;
        var approval = saga?.Steps.SingleOrDefault(step => step.StepId == "managerApproval")?.Result;
        var rejected = approval is { ValueKind: JsonValueKind.Object } result &&
            result.TryGetProperty("approved", out var approved) && approved.ValueKind == JsonValueKind.False;
        var invocation = saga?.Steps.SingleOrDefault(step => step.StepId == "invokeOrder");
        var status = saga?.Status switch
        {
            SagaStatus.Completed when rejected => "Rejected",
            SagaStatus.Completed => "Fulfilled",
            SagaStatus.Failed or SagaStatus.Compensated or SagaStatus.TimedOut => "Failed",
            SagaStatus.RecoveryRequired => "ManualReview",
            _ when purchase.StartUnconfirmed => "StartUnconfirmed",
            _ when saga?.CurrentStep == "managerApproval" => "AwaitingApproval",
            _ when saga?.CurrentStep == "invokeOrder" => "Ordering",
            _ => "Pending"
        };
        return new(id, purchase.SagaInstanceId, purchase.EmployeeId, status, saga?.Status.ToString(), saga?.CorrelationId,
            invocation?.Invocation?.Identity.InvocationId, invocation?.InvocationProgress?.TargetSagaInstanceId,
            invocation?.InvocationProgress?.Status.ToString());
    }
}

internal sealed class PurchaseStartException(Guid purchaseId, Guid? sagaId, Exception innerException)
    : Exception("Purchase request saved, but Saga startup could not be confirmed.", innerException)
{
    public Guid PurchaseId { get; } = purchaseId;
    public Guid? SagaId { get; } = sagaId;
}
