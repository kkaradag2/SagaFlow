namespace SagaFlow.Core.Abstractions;

public interface ISagaClock
{
    DateTimeOffset UtcNow { get; }
}
