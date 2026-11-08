using System.Text.Json;
using SagaFlow.Core.Messages;

namespace SagaFlow.Engine.Messaging;

/// <summary>
/// Optional target-domain materialization after admission/local claim, before acceptance publication and first dispatch.
/// May run concurrently or repeatedly: implementations must return the same execution payload for the same invocation
/// and target ID, and make their business effects idempotent. This is NOT a transaction with the Saga store or broker.
/// The original request remains in IncomingInvocation for identity/payload deduplication. Exceptions leave the target
/// pending and the request retryable; they are not business rejections (use IProcessInvocationPolicy for those).
/// </summary>
public interface IIncomingInvocationInitializer
{
    ValueTask<JsonElement> InitializeAsync(ProcessInvocationRequest request, string targetSagaInstanceId,
        CancellationToken cancellationToken);
}
