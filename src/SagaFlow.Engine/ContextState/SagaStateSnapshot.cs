using SagaFlow.Core.State;

namespace SagaFlow.Engine.ContextState;

internal static class SagaStateSnapshot
{
    public static SagaInstance Copy(SagaInstance state) => new()
    {
        Id = state.Id, DefinitionName = state.DefinitionName, DefinitionDomain = state.DefinitionDomain,
        DefinitionVersion = state.DefinitionVersion, CorrelationId = state.CorrelationId,
        CurrentStep = state.CurrentStep, Status = state.Status, Context = state.Context.Clone(), PayloadType = state.PayloadType,
        CreatedAt = state.CreatedAt, UpdatedAt = state.UpdatedAt, FailureReason = state.FailureReason, Revision = state.Revision,
        Steps = Array.AsReadOnly(state.Steps.Select(step => new SagaStepState
        {
            StepName = step.StepName, Command = step.Command, Status = step.Status, ExecutionOrder = step.ExecutionOrder,
            Input = step.Input?.Clone(), Result = step.Result?.Clone(), StartedAt = step.StartedAt, CompletedAt = step.CompletedAt,
            TimeoutLimit = step.TimeoutLimit, TimedOutAt = step.TimedOutAt
        }).ToArray()),
        Compensations = Array.AsReadOnly(state.Compensations.Select(CopyCompensation).ToArray()),
        DomainRollback = state.DomainRollback is null ? null : new SagaDomainRollbackState
        {
            Status = state.DomainRollback.Status, StartedAt = state.DomainRollback.StartedAt,
            CompletedAt = state.DomainRollback.CompletedAt, Error = state.DomainRollback.Error
        }
    };

    public static SagaCompensationState CopyCompensation(SagaCompensationState state) => new()
    {
        StepId = state.StepId, Command = state.Command, Status = state.Status, ExecutionOrder = state.ExecutionOrder,
        StartedAt = state.StartedAt, CompletedAt = state.CompletedAt, Result = state.Result?.Clone()
    };
}
