using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Engine.Messaging;
using SagaFlow.Engine.Notifications;

namespace SagaFlow.Engine.Coordination;

public sealed class SagaStepExecutor(ISagaStateStore store, CommandDispatcher dispatcher, ISagaClock clock,
    SagaStateChangePublisher? changes = null, ILocalSagaExecutionBoundary? execution = null)
{
    public void EnsureTransport() => dispatcher.EnsureTransport();

    internal SagaCommand Prepare(SagaInstance instance, string? causationId = null)
    {
        if (instance.Status != SagaStatus.Running || instance.CurrentStep is null) throw new InvalidOperationException("Saga has no active forward step.");
        var step = instance.Steps.Single(step => step.StepId == instance.CurrentStep);
        if (step.Status != SagaStepStatus.Pending) throw new InvalidOperationException($"Step '{step.StepId}' cannot be dispatched twice in the same Saga instance.");
        step.Status = SagaStepStatus.Dispatched;
        step.ExecutionOrder = instance.Steps.Max(s => s.ExecutionOrder ?? 0) + 1;
        step.Input = instance.Context.Clone();
        step.StartedAt = clock.UtcNow;
        step.Execution = ExecutionMetadata.Create(instance, step.StepId, step.Command, step.StartedAt.Value,
            dispatcher.ReplyDestination) with { CausationId = causationId };
        instance.UpdatedAt = step.StartedAt.Value;
        return new()
        {
            Name = step.Command, Payload = step.Input.Value.Clone(), TimeToLive = step.TimeoutLimit,
            Headers = step.Execution
        };
    }

    public async Task ExecuteAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        EnsureTransport();
        var command = await store.UpdateAsync(instanceId, instance => Prepare(instance), cancellationToken);
        if (changes is not null) await changes.PublishAsync(instanceId);
        await PublishPreparedAsync(command, cancellationToken);
    }

    internal async Task PublishPreparedAsync(SagaCommand command, CancellationToken cancellationToken)
    {
        try
        {
            if (execution is not null) await execution.ExecuteAsync(command, cancellationToken);
            var snapshot = await store.FindAsync(command.Headers.SagaInstanceId, cancellationToken)
                ?? throw new KeyNotFoundException("Saga disappeared before publication.");
            var step = snapshot.Steps.Single(s => s.StepId == command.Headers.StepId);
            if (step.External)
            {
                if (step.Invocation is null || step.Publication is null)
                    throw new InvalidOperationException("External step requires a handler invocation and an enabled process transport.");
                return; // Dispatched is the asynchronous external wait state; no participant command is sent.
            }
            await dispatcher.PublishAsync(command, cancellationToken);
        }
        catch (ProcessInvocationPublishException) { throw; }
        catch (Exception error)
        {
            await store.UpdateAsync(command.Headers.SagaInstanceId, instance =>
            {
                var step = instance.Steps.Single(step => step.StepId == command.Headers.StepId);
                if (step.Invocation is not null && step.Publication is not null)
                    step.Reconciliation ??= new("Local forward publication failed after invocation request was attempted; remote activity requires manual reconciliation.", clock.UtcNow);
                // A transport may synchronously deliver an outcome before returning. Never erase accepted forward history.
                if (step.Status == SagaStepStatus.Dispatched)
                {
                    step.Status = SagaStepStatus.Failed;
                    step.CompletedAt = clock.UtcNow;
                    instance.Status = SagaStatus.Failed;
                    instance.CurrentStep = null;
                    instance.FailureReason = $"Command '{command.Name}' could not be published: {error.Message}";
                    instance.UpdatedAt = clock.UtcNow;
                }
                return true;
            }, CancellationToken.None);
            if (changes is not null) await changes.PublishAsync(command.Headers.SagaInstanceId);
            throw new SagaDispatchException(command.Headers.SagaInstanceId, command.Headers.StepId, error);
        }
    }
}
