using SagaFlow.Core.Abstractions;
using SagaFlow.Core.State;
using SagaFlow.Engine.Coordination;
using Sample.DomainStarters.Return.Contracts;
using Sample.DomainStarters.Return.Data;

namespace Sample.DomainStarters.Return.Services;

internal sealed class ReturnService(
    PostgreSqlReturnRepository repository,
    ISagaFlow sagaFlow,
    ISagaStateStore state,
    ILogger<ReturnService> logger) : IReturnService
{
    public async Task<CreateReturnResponse> CreateAsync(CreateReturnRequest request, CancellationToken cancellationToken)
    {
        var entry = new ReturnCase(Guid.NewGuid(), request.OrderId, request.CustomerId.Trim(), request.Amount,
            request.Items.Select(item => new ReturnItem(item.ProductId.Trim(), item.Quantity)).ToArray(),
            request.FailStep?.Trim());
        await repository.AddAsync(entry, cancellationToken);
        logger.LogInformation("Return {ReturnId} saved for order {OrderId}", entry.ReturnId, entry.OrderId);

        Guid sagaId;
        try
        {
            // Continue Saga startup if the caller disconnects after the domain commit.
            sagaId = await sagaFlow.StartAsync(entry, CancellationToken.None);
        }
        catch (Exception error)
        {
            Guid? knownSagaId = error is SagaDispatchException dispatch &&
                Guid.TryParse(dispatch.InstanceId, out var instanceId) ? instanceId : null;
            await repository.RecordSagaStartAsync(entry.ReturnId, knownSagaId, unconfirmed: true);
            logger.LogError(error, "Saga startup could not be confirmed for return {ReturnId}, Saga {SagaInstanceId}",
                entry.ReturnId, knownSagaId);
            throw new ReturnSagaStartException(entry.ReturnId, knownSagaId, error);
        }

        await repository.RecordSagaStartAsync(entry.ReturnId, sagaId, unconfirmed: false);
        logger.LogInformation("Return {ReturnId} started Saga {SagaInstanceId}", entry.ReturnId, sagaId);
        return new(entry.ReturnId, entry.OrderId, sagaId);
    }

    public async Task<ReturnStatusResponse?> FindAsync(Guid returnId, CancellationToken cancellationToken)
    {
        var entry = await repository.FindAsync(returnId, cancellationToken);
        if (entry is null)
            return null;

        var saga = entry.SagaInstanceId is { } sagaId ? await state.FindAsync(sagaId, cancellationToken) : null;
        var refunded = saga?.Steps.Any(step => step.StepId == "payment" && step.Status == SagaStepStatus.Completed) == true;
        var rejected = saga?.Steps.Any(step => step.StepId == "rejectedNotification" && step.Status == SagaStepStatus.Completed) == true;
        var status = saga?.Status switch
        {
            SagaStatus.Completed when rejected => "Rejected",
            SagaStatus.Completed => "Accepted",
            SagaStatus.Failed or SagaStatus.Compensated or SagaStatus.TimedOut => refunded ? "ManualReview" : "Failed",
            SagaStatus.RecoveryRequired => "ManualReview",
            _ => entry.SagaStartUnconfirmed ? "StartUnconfirmed" : "Pending"
        };
        return new(returnId, entry.OrderId, entry.SagaInstanceId, entry.CustomerId, status,
            saga?.Status.ToString(), saga?.CorrelationId);
    }
}
