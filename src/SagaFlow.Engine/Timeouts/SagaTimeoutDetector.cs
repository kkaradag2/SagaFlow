using SagaFlow.Core.Abstractions;
using SagaFlow.Core.State;
using SagaFlow.Engine.Notifications;

namespace SagaFlow.Engine.Timeouts;

public sealed class SagaTimeoutDetector(ISagaStateStore store, TimeProvider timeProvider,
    SagaStateChangePublisher? changes = null)
{
    public async Task<int> DetectAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var candidates = (await store.ListAsync(cancellationToken))
            .Where(instance => instance.Status == SagaStatus.Running && instance.CurrentStep is not null)
            .Select(instance => (Instance: instance, Step: instance.Steps.SingleOrDefault(step =>
                step.StepId == instance.CurrentStep && IsExpired(step, now))))
            .Where(candidate => candidate.Step is not null)
            .Select(candidate => (candidate.Instance.Id, StepId: candidate.Step!.StepId))
            .ToArray();

        var detected = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var applied = await store.UpdateAsync(candidate.Id, instance =>
            {
                if (instance.Status != SagaStatus.Running || instance.CurrentStep != candidate.StepId) return false;
                var step = instance.Steps.Single(step => step.StepId == candidate.StepId);
                if (!IsExpired(step, now)) return false;
                step.Status = SagaStepStatus.TimedOut;
                step.TimedOutAt = now;
                instance.Status = SagaStatus.TimedOut;
                instance.UpdatedAt = now;
                return true;
            }, cancellationToken);
            if (applied)
            {
                detected++;
                if (changes is not null) await changes.PublishAsync(candidate.Id);
            }
        }
        return detected;
    }

    private static bool IsExpired(SagaStepState step, DateTimeOffset now) =>
        step.Status == SagaStepStatus.Dispatched
        && step.StartedAt is { } startedAt
        && step.TimeoutLimit is { } timeoutLimit
        && now >= startedAt
        && now - startedAt >= timeoutLimit;
}
