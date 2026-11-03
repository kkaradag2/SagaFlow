using SagaFlow.Core.Messages;
using SagaFlow.Participants.Messaging;

namespace SagaFlow.Warehouse;

public sealed class WarehouseCommandHandler(WarehouseRepository repository, TimeProvider timeProvider) : IParticipantCommandHandler
{
    public IReadOnlyCollection<string> CommandNames { get; } = ["InspectReturn"];
    public ParticipantCommandResult? Handle(SagaCommand command, string? replyTo = null)
    {
        if (command.Name != "InspectReturn" || command.Headers.IsCompensation)
            throw new InvalidOperationException("Only forward InspectReturn is supported.");
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Headers.SagaInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Headers.CorrelationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Headers.StepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(replyTo);
        if (SampleFailureInjection.IsRequested(command, "warehouse", "InspectReturn"))
            return ParticipantCommandResult.Failure("warehouse");
        repository.Register(new InspectionRecord(
            command.Payload.GetProperty("returnId").GetGuid(),
            command.Payload.GetProperty("orderId").GetGuid(),
            command.Headers.SagaInstanceId, command.Headers.CorrelationId,
            command.Headers.StepId, replyTo, timeProvider.GetUtcNow()));
        return null;
    }
}
