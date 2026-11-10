using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Engine.Coordination;
using SagaFlow.Engine.Messaging;

namespace SagaFlow.Engine.Consistency;

public sealed class CompensationManager(
    CommandDispatcher dispatcher,
    ISagaClock clock)
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
