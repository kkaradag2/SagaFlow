using SagaFlow.Core.Messages;
using SagaFlow.ManagerApproval.Domain;
using SagaFlow.ManagerApproval.Repositories;
using SagaFlow.Participants.Messaging;

namespace SagaFlow.ManagerApproval.Messaging;

public sealed class ApprovalCommandHandler(InMemoryApprovalRepository approvals, TimeProvider timeProvider) : IParticipantCommandHandler
{
    public IReadOnlyCollection<string> CommandNames { get; } = ["RequestManagerApproval"];

    public ParticipantCommandResult? Handle(SagaCommand command, string? replyTo = null)
    {
        if (command.Name != "RequestManagerApproval" || command.Headers.IsCompensation)
            throw new InvalidOperationException("Only forward RequestManagerApproval commands are supported.");
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Headers.SagaInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Headers.CorrelationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Headers.StepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(replyTo);
        var quantity = command.Payload.GetProperty("totalQuantity").GetInt64();
        if (quantity <= 0) throw new InvalidOperationException("Total quantity must be positive.");
        approvals.Register(new ApprovalRecord(command.Payload.GetProperty("id").GetGuid(), quantity,
            command.Headers.SagaInstanceId, command.Headers.CorrelationId, command.Headers.StepId,
            replyTo, timeProvider.GetUtcNow()));
        return null; // The shared worker ACKs acceptance; the API will publish the outcome later.
    }
}
