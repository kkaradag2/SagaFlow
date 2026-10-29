using System.Reflection;
using System.Text.Json;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.State;
using SagaFlow.Engine.Notifications;

namespace SagaFlow.Engine.Consistency;

public sealed class DomainRollbackManager(ISagaStateStore store, IServiceProvider services, ISagaClock clock,
    SagaStateChangePublisher? changes = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task ExecuteIfPlannedAsync(string instanceId, CancellationToken cancellationToken)
    {
        // The outcome is already accepted. Request cancellation may cancel the handler,
        // but must not interrupt its state claim or leave rollback finalization unfinished.
        var snapshot = await store.FindAsync(instanceId, CancellationToken.None);
        if (snapshot?.DomainRollback?.Status != SagaDomainRollbackStatus.Planned) return;
        var payloadType = snapshot.PayloadType is null ? null : Type.GetType(snapshot.PayloadType, throwOnError: false);
        var handlerType = payloadType is null ? null : typeof(SagaRollbackHandler<>).MakeGenericType(payloadType);
        if (handlerType is null)
        {
            await store.UpdateAsync(instanceId, instance =>
            {
                if (instance.Status != SagaStatus.Compensating || instance.DomainRollback?.Status != SagaDomainRollbackStatus.Planned)
                    return false;
                instance.DomainRollback = null;
                instance.Status = SagaStatus.Compensated;
                instance.UpdatedAt = clock.UtcNow;
                return true;
            }, CancellationToken.None);
            if (changes is not null) await changes.PublishAsync(instanceId);
            return;
        }

        var claimed = await store.UpdateAsync(instanceId, instance =>
        {
            if (instance.Status != SagaStatus.Compensating || instance.DomainRollback?.Status != SagaDomainRollbackStatus.Planned)
                return false;
            instance.DomainRollback.Status = SagaDomainRollbackStatus.Executing;
            instance.DomainRollback.StartedAt = clock.UtcNow;
            instance.UpdatedAt = clock.UtcNow;
            return true;
        }, CancellationToken.None);
        if (!claimed) return;
        if (changes is not null) await changes.PublishAsync(instanceId);

        try
        {
            var handler = services.GetService(handlerType!);
            if (handler is null)
            {
                await store.UpdateAsync(instanceId, instance =>
                {
                    if (instance.DomainRollback?.Status != SagaDomainRollbackStatus.Executing) return false;
                    instance.DomainRollback = null;
                    instance.Status = SagaStatus.Compensated;
                    instance.UpdatedAt = clock.UtcNow;
                    return true;
                }, CancellationToken.None);
                if (changes is not null) await changes.PublishAsync(instanceId);
                return;
            }
            var payload = JsonSerializer.Deserialize(snapshot.Context, payloadType!, JsonOptions);
            var method = handlerType!.GetMethod(nameof(SagaRollbackHandler<object>.RollbackAsync))!;
            try { await (Task)method.Invoke(handler, [payload, cancellationToken])!; }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }

            await store.UpdateAsync(instanceId, instance =>
            {
                if (instance.DomainRollback?.Status != SagaDomainRollbackStatus.Executing) return false;
                var now = clock.UtcNow;
                instance.DomainRollback.Status = SagaDomainRollbackStatus.Completed;
                instance.DomainRollback.CompletedAt = now;
                instance.Status = SagaStatus.Compensated;
                instance.UpdatedAt = now;
                return true;
            }, CancellationToken.None);
        }
        catch (Exception error)
        {
            await store.UpdateAsync(instanceId, instance =>
            {
                if (instance.DomainRollback?.Status != SagaDomainRollbackStatus.Executing) return false;
                var now = clock.UtcNow;
                instance.DomainRollback.Status = SagaDomainRollbackStatus.Failed;
                instance.DomainRollback.CompletedAt = now;
                instance.DomainRollback.Error = error.Message;
                instance.Status = SagaStatus.Failed;
                instance.FailureReason = $"Domain starter rollback failed: {error.Message}";
                instance.UpdatedAt = now;
                return true;
            }, CancellationToken.None);
        }
        if (changes is not null) await changes.PublishAsync(instanceId);
    }
}
