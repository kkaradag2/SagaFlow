using SagaFlow.Core.Messages;
namespace SagaFlow.Engine.Coordination;

/// <summary>Runs in a fresh Domain Starter DI scope before a forward command is published.</summary>
public interface ILocalSagaStepHandler
{
    Task ExecuteAsync(SagaCommand command, CancellationToken cancellationToken);
}
public interface ILocalSagaExecutionBoundary
{
    bool HasLocalStepHandlers => true;
    Task ExecuteAsync(SagaCommand command, CancellationToken cancellationToken);
}
public sealed class InvocationExecutionContext
{
    internal string? InstanceId { get; private set; }
    internal string? StepId { get; private set; }
    internal void Bind(SagaCommand command) { InstanceId = command.Headers.SagaInstanceId; StepId = command.Headers.StepId; }
    internal void Clear() { InstanceId = null; StepId = null; }
}
