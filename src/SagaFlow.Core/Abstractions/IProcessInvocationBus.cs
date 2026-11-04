using SagaFlow.Core.Messages;
namespace SagaFlow.Core.Abstractions;

/// <summary>Domain-to-domain transport, separate from participant commands. Completion means transport publish completed only.</summary>
public interface IProcessInvocationBus
{
    ValueTask PublishAsync(ProcessInvocationRequest request, CancellationToken cancellationToken = default);
    ValueTask PublishAsync(ProcessInvocationAccepted accepted, CancellationToken cancellationToken = default);
    ValueTask PublishAsync(ProcessInvocationRejected rejected, CancellationToken cancellationToken = default);
    ValueTask PublishAsync(ProcessInvocationTerminalOutcome outcome, CancellationToken cancellationToken = default);
}
