namespace SagaFlow.Core.Definitions;

/// <summary>Declarative start-event identity only; no subscription or execution behavior.</summary>
public sealed class SagaTriggerDefinition
{
    public string Event { get; init; } = string.Empty;
}
