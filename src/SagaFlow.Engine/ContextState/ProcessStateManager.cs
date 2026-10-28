using SagaFlow.Core.Messages;
using SagaFlow.Core.Definitions;
using SagaFlow.Core.State;

namespace SagaFlow.Engine.ContextState;

public sealed class ProcessStateManager
{
    public void ApplyOutcome(SagaInstance instance, SagaOutcomeEvent outcome, string target, DateTimeOffset now, SagaDefinition definition)
    {
        var step = instance.Steps.Single(step => step.StepId == outcome.Headers.StepId);
        step.Status = outcome.IsSuccess ? SagaStepStatus.Completed : SagaStepStatus.Failed;
        step.Result = outcome.Result.Clone();
        step.CompletedAt = now;
        instance.UpdatedAt = now;
        instance.CurrentStep = target is "completed" or "compensation" ? null : target;
        if (target == "completed") instance.Status = SagaStatus.Completed;
        if (target == "compensation")
        {
            instance.Status = SagaStatus.CompensationRequested;
            var compensationCommands = definition.Steps
                .Where(s => s.Compensation is not null)
                .ToDictionary(s => s.Name, s => s.Compensation!.Command, StringComparer.Ordinal);
            var eligible = instance.Steps.Where(s => s.Status == SagaStepStatus.Completed)
                .OrderByDescending(s => s.ExecutionOrder).ToArray();
            var order = 0;
            foreach (var completed in eligible)
            {
                if (!compensationCommands.TryGetValue(completed.StepId, out var command)) continue;
                var compensation = new SagaCompensationState
                {
                    StepId = completed.StepId,
                    Command = command,
                    Status = SagaCompensationStatus.Planned,
                    ExecutionOrder = ++order
                };
                instance.Compensations = Array.AsReadOnly([.. instance.Compensations, compensation]);
            }
        }
    }

    public IReadOnlyList<SagaCompensationState> GetCompensationPlan(SagaInstance instance) => Array.AsReadOnly(instance.Compensations
        .Where(c => c.Status == SagaCompensationStatus.Planned).OrderBy(c => c.ExecutionOrder)
        .Select(SagaStateSnapshot.CopyCompensation).ToArray());
}
