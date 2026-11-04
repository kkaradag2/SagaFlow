using SagaFlow.Core.Messages;

namespace SagaFlow.Core.Abstractions;

public interface ISagaFlow
{
    Task<Guid> StartAsync<TPayload>(TPayload payload, CancellationToken cancellationToken = default);

    /// <summary>Requests invocation of a declared external process. A receipt never implies acceptance or completion.</summary>
    Task<ProcessInvocationReceipt> InvokeAsync<TPayload>(string target, TPayload payload, CancellationToken cancellationToken = default);
}
