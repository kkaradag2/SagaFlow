using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Engine.Messaging;

namespace SagaFlow.Engine.Coordination;

public sealed class SagaStepExecutor(ISagaStateStore store, CommandDispatcher dispatcher, ISagaClock clock)
{
    public void EnsureTransport() => dispatcher.EnsureTransport();

    internal SagaCommand Prepare(SagaInstance instance)
    {
        if (instance.Status != SagaStatus.Running || instance.CurrentStep is null) throw new InvalidOperationException("Saga has no active forward step.");
        var step = instance.Steps.Single(step => step.StepId == instance.CurrentStep);
        if (step.Status != SagaStepStatus.Pending) throw new InvalidOperationException($"Step '{step.StepId}' cannot be dispatched twice in the same Saga instance.");
        step.Status = SagaStepStatus.Dispatched;
        step.ExecutionOrder = instance.Steps.Max(s => s.ExecutionOrder ?? 0) + 1;
        step.Input = instance.Context.Clone();
        step.StartedAt = clock.UtcNow;
        instance.UpdatedAt = step.StartedAt.Value;
        return new()
        {
            Name = step.Command, Payload = step.Input.Value.Clone(), TimeToLive = step.TimeoutLimit,
            Headers = new() { SagaInstanceId = instance.Id, CorrelationId = instance.CorrelationId, StepId = step.StepId }
        };
    }

    public async Task ExecuteAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        EnsureTransport();
        var command = await store.UpdateAsync(instanceId, Prepare, cancellationToken);
        await PublishPreparedAsync(command, cancellationToken);
    }

    internal async Task PublishPreparedAsync(SagaCommand command, CancellationToken cancellationToken)
    {
        try { await dispatcher.PublishAsync(command, cancellationToken); }
        catch (Exception error)
        {
            await store.UpdateAsync(command.Headers.SagaInstanceId, instance =>
            {
                var step = instance.Steps.Single(step => step.StepId == command.Headers.StepId);
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
            throw new SagaDispatchException(command.Headers.SagaInstanceId, command.Headers.StepId, error);
        }
    }
}
