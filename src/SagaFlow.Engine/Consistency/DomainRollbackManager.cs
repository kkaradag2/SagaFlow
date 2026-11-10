using System.Reflection;
using System.Text.Json;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.State;
using SagaFlow.Engine.Notifications;
using SagaFlow.Engine.Messaging;

namespace SagaFlow.Engine.Consistency;

public sealed class DomainRollbackManager(ISagaStateStore store, IServiceProvider services, ISagaClock clock,
    SagaStateChangePublisher? changes = null, ProcessInvocationPublisher? invocationPublisher = null)
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
            await UpdateAsync(instanceId, instance =>
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

        object? handler = null;
        Exception? failure = null;
        try
        {
            handler = services.GetService(handlerType);
            if (handler is not null)
            {
                var payload = JsonSerializer.Deserialize(snapshot.Context, payloadType!, JsonOptions);
                var method = handlerType.GetMethod(nameof(SagaRollbackHandler<object>.RollbackAsync))!;
                await (Task)method.Invoke(handler, [payload, cancellationToken])!;
            }
        }
        catch (TargetInvocationException error) when (error.InnerException is not null) { failure = error.InnerException; }
        catch (Exception error) { failure = error; }

        await UpdateAsync(instanceId, instance =>
        {
            if (instance.DomainRollback?.Status != SagaDomainRollbackStatus.Executing) return false;
            var now = clock.UtcNow;
            instance.UpdatedAt = now;
            if (failure is null)
            {
                if (handler is null) instance.DomainRollback = null;
                else
                {
                    instance.DomainRollback.Status = SagaDomainRollbackStatus.Completed;
                    instance.DomainRollback.CompletedAt = now;
                }
                instance.Status = SagaStatus.Compensated;
            }
            else
            {
                instance.DomainRollback.Status = SagaDomainRollbackStatus.Failed;
                instance.DomainRollback.CompletedAt = now;
                instance.DomainRollback.Error = failure.Message;
                instance.Status = SagaStatus.Failed;
                instance.FailureReason = $"Domain starter rollback failed: {failure.Message}";
            }
            return true;
        }, CancellationToken.None);
        if (changes is not null) await changes.PublishAsync(instanceId);
    }

    private ValueTask<bool> UpdateAsync(string id, Func<SagaInstance, bool> update, CancellationToken ct) =>
        store.UpdateWithOutboxAsync(id, instance =>
        {
            var changed = update(instance);
            return (changed, changed ? invocationPublisher?.PrepareTerminal(instance) : null);
        }, ct);
}
