using System.Text.Json;

namespace SagaFlow.Core.Messages;

public sealed class SagaCommand
{
    public const int MaximumRecoverySnapshotBytes = 64 * 1024;
    public string Name { get; init; } = string.Empty;
    public JsonElement Payload { get; init; } = JsonSerializer.SerializeToElement<object?>(null);
    public TimeSpan? TimeToLive { get; init; }

    public SagaMessageHeaders Headers { get; init; } = new();

    /// <summary>Engine-owned state at this dispatch, carried only in AMQP execution metadata.</summary>
    public byte[]? RecoverySnapshot { get; init; }
}
