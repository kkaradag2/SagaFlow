using SagaFlow.Core.Messages;
using SagaFlow.Core.State;

namespace SagaFlow.Engine.ContextState;

public sealed class ProcessStateManager
{
    public void ApplyOutcome(SagaInstance instance, SagaOutcomeEvent outcome, string target, DateTimeOffset now)
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
            var eligible = instance.Steps.Where(s => s.Status == SagaStepStatus.Completed)
                .OrderByDescending(s => s.ExecutionOrder).ToArray();
            var order = 0;
            foreach (var completed in eligible)
            {
                var compensation = instance.Compensations.SingleOrDefault(c => c.StepId == completed.StepId);
                if (compensation is null) continue;
                compensation.Status = SagaCompensationStatus.Planned;
                compensation.ExecutionOrder = ++order;
            }
        }
    }

    public IReadOnlyList<SagaCompensationState> GetCompensationPlan(SagaInstance instance) => Array.AsReadOnly(instance.Compensations
        .Where(c => c.Status == SagaCompensationStatus.Planned).OrderBy(c => c.ExecutionOrder)
        .Select(SagaStateSnapshot.CopyCompensation).ToArray());
}
