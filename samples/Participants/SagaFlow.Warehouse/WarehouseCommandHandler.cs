using SagaFlow.Participants.Messaging;

namespace SagaFlow.Warehouse;

public sealed class WarehouseCommandHandler(WarehouseRepository repository, TimeProvider timeProvider, DeferredReplies replies) : IParticipantCommandHandler
{
    public IReadOnlyCollection<string> CommandNames { get; } = ["InspectReturn"];
    public ParticipantCommandResult? Handle(ParticipantCommand command, string? replyTo = null)
    {
        if (command.Name != "InspectReturn" || command.Headers.IsCompensation)
            throw new InvalidOperationException("Only forward InspectReturn is supported.");
        if (SampleFailureInjection.IsRequested(command, "warehouse", "InspectReturn"))
            return ParticipantCommandResult.Failure("warehouse");
        var request = command.Bind<InspectReturnRequest>();
        replies.Register(command, replyTo, () => repository.Register(new InspectionRecord(
            request.ReturnId, request.OrderId,
            timeProvider.GetUtcNow())).InspectionId);
        return null;
    }
}
