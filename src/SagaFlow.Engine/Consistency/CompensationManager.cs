using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Engine.Coordination;
using SagaFlow.Engine.Messaging;
using SagaFlow.Engine.Notifications;

namespace SagaFlow.Engine.Consistency;

public sealed class CompensationManager(
    ISagaStateStore store,
    CommandDispatcher dispatcher,
    ISagaClock clock,
    SagaStateChangePublisher? changes = null)
{
    public SagaCommand? Begin(SagaInstance instance, string? causationId = null)
    {
        if (instance.Status != SagaStatus.CompensationRequested)
            throw new InvalidOperationException("Saga has no requested compensation plan.");

        return PrepareNext(instance, causationId);
    }

    public SagaCommand? ApplyOutcome(SagaInstance instance, SagaOutcomeEvent outcome)
    {
        var compensation = instance.Compensations.SingleOrDefault(item => item.StepId == outcome.Headers.StepId)
            ?? throw new InvalidSagaOutcomeException("Outcome refers to an unknown Saga compensation step.");
        ExecutionMetadata.Validate(compensation.Execution, outcome);

        if (compensation.Result.HasValue && compensation.Status is SagaCompensationStatus.Completed or SagaCompensationStatus.Failed)
            throw new DuplicateSagaOutcomeException("Compensation outcome was already processed; duplicate outcomes cannot advance the plan again.");
        if (instance.Status != SagaStatus.Compensating || compensation.Status != SagaCompensationStatus.Dispatched)
            throw new InvalidSagaOutcomeException("Outcome does not refer to the active dispatched Saga compensation.");

        var now = clock.UtcNow;
        compensation.Result = outcome.Result.Clone();
        compensation.CompletedAt = now;
        compensation.Status = outcome.IsSuccess ? SagaCompensationStatus.Completed : SagaCompensationStatus.Failed;
        instance.UpdatedAt = now;

        if (!outcome.IsSuccess)
        {
            instance.Status = SagaStatus.Failed;
            return null;
        }

        return PrepareNext(instance, outcome.MessageId);
    }

    public async Task PublishPreparedAsync(SagaCommand command, CancellationToken cancellationToken)
    {
        try { await dispatcher.PublishAsync(command, cancellationToken); }
        catch (Exception error)
        {
            await store.UpdateAsync(command.Headers.SagaInstanceId, instance =>
            {
                var compensation = instance.Compensations.Single(item => item.StepId == command.Headers.StepId);
                // A transport may synchronously deliver an outcome before returning. Never erase accepted compensation history.
                if (compensation.Status == SagaCompensationStatus.Dispatched)
                {
                    var now = clock.UtcNow;
                    compensation.Status = SagaCompensationStatus.Failed;
                    compensation.CompletedAt = now;
                    instance.Status = SagaStatus.Failed;
                    instance.UpdatedAt = now;
                    instance.FailureReason ??= $"Compensation command '{command.Name}' could not be published: {error.Message}";
                }
                return true;
            }, CancellationToken.None);
            if (changes is not null) await changes.PublishAsync(command.Headers.SagaInstanceId);
            throw new SagaDispatchException(command.Headers.SagaInstanceId, command.Headers.StepId, error);
        }
    }

    private SagaCommand? PrepareNext(SagaInstance instance, string? causationId)
    {
        var next = instance.Compensations
            .Where(item => item.Status == SagaCompensationStatus.Planned)
            .OrderBy(item => item.ExecutionOrder)
            .FirstOrDefault();
        if (next is null)
        {
            instance.DomainRollback ??= new SagaDomainRollbackState();
            instance.Status = SagaStatus.Compensating;
            instance.UpdatedAt = clock.UtcNow;
            return null;
        }

        var now = clock.UtcNow;
        next.Status = SagaCompensationStatus.Dispatched;
        next.StartedAt = now;
        next.Execution = ExecutionMetadata.Create(instance, next.StepId, next.Command, now,
            dispatcher.ReplyDestination, compensation: true) with { CausationId = causationId };
        instance.Status = SagaStatus.Compensating;
        instance.UpdatedAt = now;
        return new()
        {
            Name = next.Command,
            Payload = instance.Context.Clone(),
            Headers = next.Execution
        };
    }
}
