using SagaFlow.Core.Messages;
using SagaFlow.Core.State;

namespace SagaFlow.Engine.Messaging;

internal static class OutboxPreparation
{
    internal static OutboxMessage? Create(SagaInstance instance, SagaCommand? command, string? exchange,
        bool requiresLocalPreparation)
    {
        if (command is null || !command.Headers.IsCompensation &&
            instance.Steps.Single(step => step.StepId == command.Headers.StepId).External)
            return null;
        // Both stores commit one new revision after the callback. Evidence must describe that revision.
        instance.Revision++;
        return OutboxMessage.FromCommand(new SagaCommand
        {
            Name = command.Name, Payload = command.Payload, TimeToLive = command.TimeToLive,
            Headers = command.Headers, RecoverySnapshot = SagaExecutionSnapshotCodec.Capture(instance)
        }, exchange) with
        {
            PreparationOwner = requiresLocalPreparation && !command.Headers.IsCompensation ? Owner(command) : null
        };
    }

    internal static string Owner(SagaCommand command) => $"local-preparation:{command.Headers.CommandId}";
}
