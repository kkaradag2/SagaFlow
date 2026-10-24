using SagaFlow.Core.Abstractions;

namespace SagaFlow.Engine.ContextState;

public sealed class SystemSagaClock : ISagaClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
