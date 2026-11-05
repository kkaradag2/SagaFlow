using SagaFlow.Participants.Messaging;
using SagaFlow.ManagerApproval.Domain;
using SagaFlow.ManagerApproval.Repositories;

namespace SagaFlow.ManagerApproval.Messaging;

public sealed class ApprovalCommandHandler(InMemoryApprovalRepository approvals, TimeProvider timeProvider, DeferredReplies replies) : IParticipantCommandHandler
{
    public IReadOnlyCollection<string> CommandNames { get; } = ["RequestManagerApproval"];

    public ParticipantCommandResult? Handle(ParticipantCommand command, string? replyTo = null)
    {
        if (command.Name != "RequestManagerApproval" || command.Headers.IsCompensation)
            throw new InvalidOperationException("Only forward RequestManagerApproval commands are supported.");
        var request = command.Bind<RequestManagerApprovalRequest>();
        var quantity = request.TotalQuantity;
        if (quantity <= 0) throw new InvalidOperationException("Total quantity must be positive.");
        replies.Register(command, replyTo, () => approvals.Register(new ApprovalRecord(
            request.Id, quantity, timeProvider.GetUtcNow())).ApprovalId);
        return null; // The shared worker ACKs acceptance; the API will publish the outcome later.
    }
}
