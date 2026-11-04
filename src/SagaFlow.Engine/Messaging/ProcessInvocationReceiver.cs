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
    ProcessInvocationManager manager, IProcessInvocationBus bus, ISagaClock clock, IEnumerable<IProcessInvocationPolicy> policies)
{
    public async Task ReceiveAsync(ProcessInvocationRequest request, CancellationToken cancellationToken = default)
    {
        request.Message.ValidateAgainst(request.Identity);
        var target = request.Identity.Target;
        if (definitions.Find(new(target.Domain, target.Process, target.Version))?.Origin != DefinitionOrigin.Local)
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
            decision = await store.RecordIncomingDecisionAsync(new(request, reason,
                new(request.Identity, Guid.NewGuid().ToString("N"), clock.UtcNow)), cancellationToken);
        }
        if (decision.Request.Identity != request.Identity || decision.Request.Payload.GetRawText() != request.Payload.GetRawText())
            throw new InvalidOperationException("Duplicate invocation identity/payload conflict.");
        if (decision.RejectionReason is { } rejected)
            await bus.PublishAsync(new ProcessInvocationRejected(decision.Reply, rejected), cancellationToken);
        else
        {
            var instance = await manager.GetOrCreateTargetAsync(request, cancellationToken);
            await bus.PublishAsync(new ProcessInvocationAccepted(decision.Reply, instance.Id), cancellationToken);
        }
    }

    public async Task ReceiveAsync(ProcessInvocationAccepted accepted, CancellationToken cancellationToken = default)
    {
        var current = await ValidateOutcomeAsync(accepted.Message, cancellationToken);
        await manager.ObserveAsync(accepted.Message.Identity, ExternalProcessStatus.Accepted, accepted.TargetSagaInstanceId, cancellationToken,
            accepted.Message, Fingerprint(accepted));
    }
    public async Task ReceiveAsync(ProcessInvocationRejected rejected, CancellationToken cancellationToken = default)
    {
        await ValidateOutcomeAsync(rejected.Message, cancellationToken);
        await manager.ObserveAsync(rejected.Message.Identity, ExternalProcessStatus.Rejected, cancellationToken: cancellationToken, message: rejected.Message, fingerprint: Fingerprint(rejected));
    }
    public async Task ReceiveAsync(ProcessInvocationTerminalOutcome outcome, CancellationToken cancellationToken = default)
    {
        var current = await ValidateOutcomeAsync(outcome.Message, cancellationToken);
        if (current.LastKnownStatus == ExternalProcessStatus.Requested)
            throw new InvocationOutcomeNotReadyException("Terminal outcome must wait for acceptance; redeliver or replay acceptance.");
        await manager.ObserveAsync(outcome.Message.Identity, outcome.Status, outcome.TargetSagaInstanceId, cancellationToken, outcome.Message, Fingerprint(outcome));
    }
    private static string Fingerprint<T>(T message) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(message)));
    private async Task<CrossDomainInstanceRelationship> ValidateOutcomeAsync(InvocationMessage message, CancellationToken cancellationToken)
    {
        var current = await manager.FindAsync(message.Identity.SourceSagaInstanceId, message.Identity.InvocationId, cancellationToken);
        message.ValidateAgainst(current.Identity);
        return current;
    }
}
