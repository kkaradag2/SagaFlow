using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Engine.Coordination;
using SagaFlow.Engine.ProcessDefinition;
namespace SagaFlow.Engine.Messaging;

/// <summary>Domain-owned, side-effect-free admission validation. Null accepts; a nonempty reason rejects.
/// Concurrent duplicate deliveries may evaluate validation more than once; the first committed decision wins.</summary>
public interface IProcessInvocationPolicy
{
    ValueTask<string?> ValidateAsync(ProcessInvocationRequest request, CancellationToken cancellationToken);
}
public sealed class InvocationOutcomeNotReadyException(string message) : Exception(message);

public sealed class ProcessInvocationReceiver(ISagaStateStore store, ProcessDefinitionRegistry definitions,
    ProcessInvocationManager manager, IProcessInvocationBus bus, ISagaClock clock, IEnumerable<IProcessInvocationPolicy> policies, SagaCoordinator coordinator,
    CommandDispatcher dispatcher, IIncomingInvocationInitializer? initializer = null)
{

    public async Task ReceiveAsync(ProcessInvocationRequest request, CancellationToken cancellationToken = default)
    {
        request.Message.ValidateAgainst(request.Identity);
        var target = request.Identity.Target;
        var definition = definitions.Find(new(target.Domain, target.Process, target.Version));
        if (definition?.Origin != DefinitionOrigin.Local)
            throw new InvalidOperationException("Request does not address a locally owned target definition/version.");
        var decision = await store.FindIncomingDecisionAsync(request.Identity.InvocationId, cancellationToken);
        if (decision is null)
        {
            string? reason = null;
            var validators = policies.ToArray();
            // Fail closed when the domain has not registered its admission policy.
            if (validators.Length == 0) reason = "Target domain has no invocation admission policy.";
            foreach (var policy in validators)
            {
                try { reason = await policy.ValidateAsync(request, cancellationToken); }
                catch (Exception error) { throw new IOException("Domain admission validation failed; no admission decision was committed.", error); }
                if (reason is not null)
                {
                    if (string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Business rejection reason must not be empty.");
                    break;
                }
            }
            decision = new(request, reason, new(request.Identity, Guid.NewGuid().ToString("N"), clock.UtcNow, request.Message.MessageId));
        }
        // Always pass the actual delivery so the store can reject conflicting duplicates under the claim lock.
        var admission = await store.AdmitWithOutboxAsync(decision with { Request = request },
            () => new SagaInstanceManager(clock).Create(definition.Definition, request.Payload),
            (stored, targetId) => bus.PrepareOutbox(targetId is null
                ? OutboxMessage.FromInvocation(new ProcessInvocationRejected(stored.Reply, stored.RejectionReason!))
                : OutboxMessage.FromInvocation(new ProcessInvocationAccepted(stored.Reply, targetId)) with
                {
                    PreparationOwner = initializer is null ? null : InitializationOwner(stored.Reply.MessageId)
                }), cancellationToken);
        if (admission.TargetId is { } id && initializer is not null)
        {
            var instance = await store.FindAsync(id, cancellationToken) ?? throw new InvalidOperationException("Admitted target is missing.");
            if (instance.Status == SagaStatus.Running && instance.Steps.All(s => s.Status == SagaStepStatus.Pending))
            {
                var payload = await initializer.InitializeAsync(request, id, cancellationToken);
                await manager.InitializeIncomingPayloadAsync(id, request, payload, cancellationToken);
            }
            var responseId = admission.Decision.Reply.MessageId;
            try { await store.ReleaseOutboxAsync(responseId, InitializationOwner(responseId), DateTimeOffset.UtcNow, cancellationToken); }
            catch (OutboxClaimLostException) { /* A matching delivery may already have released this response. */ }
        }
        await dispatcher.DispatchOutboxAsync(store, cancellationToken);
        if (admission.TargetId is { } targetId)
            await coordinator.StartIncomingAsync(targetId, cancellationToken);
    }

    private static string InitializationOwner(string messageId) => $"incoming-initialization:{messageId}";

    public async Task ReceiveAsync(ProcessInvocationAccepted accepted, CancellationToken cancellationToken = default)
    {
        var current = await ValidateOutcomeAsync(accepted.Message, cancellationToken);
        await manager.ObserveAsync(accepted.Message.Identity, ExternalProcessStatus.Accepted, accepted.TargetSagaInstanceId, cancellationToken,
            accepted.Message, Fingerprint(accepted));
    }
    public async Task ReceiveAsync(ProcessInvocationRejected rejected, CancellationToken cancellationToken = default)
    {
        await ValidateOutcomeAsync(rejected.Message, cancellationToken);
        await manager.ObserveAsync(rejected.Message.Identity, ExternalProcessStatus.Rejected, cancellationToken: cancellationToken, message: rejected.Message, fingerprint: Fingerprint(rejected), rejectionReason: rejected.Reason);
        await coordinator.HandleExternalRejectedAsync(rejected, cancellationToken);
    }
    public async Task ReceiveAsync(ProcessInvocationTerminalOutcome outcome, CancellationToken cancellationToken = default)
    {
        var current = await ValidateOutcomeAsync(outcome.Message, cancellationToken);
        if (current.LastKnownStatus == ExternalProcessStatus.Requested)
            throw new InvocationOutcomeNotReadyException("Terminal outcome must wait for acceptance; redeliver or replay acceptance.");
        await manager.ObserveAsync(outcome.Message.Identity, outcome.Status, outcome.TargetSagaInstanceId, cancellationToken, outcome.Message, Fingerprint(outcome));
        await coordinator.HandleExternalTerminalAsync(outcome, cancellationToken);
    }
    private static string Fingerprint<T>(T message) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(message)));
    private async Task<CrossDomainInstanceRelationship> ValidateOutcomeAsync(InvocationMessage message, CancellationToken cancellationToken)
    {
        var current = await manager.FindAsync(message.Identity.SourceSagaInstanceId, message.Identity.InvocationId, cancellationToken);
        message.ValidateAgainst(current.Identity);
        return current;
    }
}
