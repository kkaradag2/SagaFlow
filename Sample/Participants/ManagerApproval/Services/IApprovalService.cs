using Sample.Participants.ManagerApproval.Domain;

namespace Sample.Participants.ManagerApproval.Services;

public interface IApprovalService
{
    Task<IReadOnlyList<ApprovalRecord>> GetPendingAsync(CancellationToken cancellationToken);

    Task<ApprovalRecord?> FindAsync(Guid approvalId, CancellationToken cancellationToken);

    Task<ApprovalRecord?> DecideAsync(Guid approvalId, bool approved, CancellationToken cancellationToken);
}
