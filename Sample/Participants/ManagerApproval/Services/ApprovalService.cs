using Sample.Participants.ManagerApproval.Data;
using Sample.Participants.ManagerApproval.Domain;

namespace Sample.Participants.ManagerApproval.Services;

internal sealed class ApprovalService(ApprovalRepository approvals, ApprovalOutbox outbox) : IApprovalService
{
    public Task<IReadOnlyList<ApprovalRecord>> GetPendingAsync(CancellationToken cancellationToken)
        => approvals.GetPendingAsync(cancellationToken);

    public Task<ApprovalRecord?> FindAsync(Guid approvalId, CancellationToken cancellationToken)
        => approvals.FindAsync(approvalId, cancellationToken);

    public async Task<ApprovalRecord?> DecideAsync(Guid approvalId, bool approved, CancellationToken cancellationToken)
    {
        var record = await approvals.DecideAsync(approvalId, approved, cancellationToken);
        if (record?.Approved != approved || record.PublishedAt is not null)
            return record;

        await outbox.TryPublishAsync(approvalId, cancellationToken);
        return await approvals.FindAsync(approvalId, cancellationToken);
    }
}
