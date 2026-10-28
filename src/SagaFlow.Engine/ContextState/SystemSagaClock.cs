using SagaFlow.Core.Abstractions;

namespace SagaFlow.Engine.ContextState;

public sealed class SystemSagaClock(TimeProvider timeProvider) : ISagaClock
{
    public SystemSagaClock() : this(TimeProvider.System) { }

    public DateTimeOffset UtcNow => timeProvider.GetUtcNow();
}
