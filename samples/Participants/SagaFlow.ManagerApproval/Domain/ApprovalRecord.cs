namespace SagaFlow.ManagerApproval.Domain;

public enum ApprovalStatus { Pending, Publishing, Approved, Rejected, PublishFailed, PublishNotSent }

public sealed record ApprovalRecord(
    Guid OrderId, long TotalQuantity, DateTimeOffset RequestedAt)
{
    public Guid ApprovalId { get; init; } = Guid.NewGuid();
    public ApprovalStatus Status { get; init; } = ApprovalStatus.Pending;
    public string? Decision { get; init; }
    public DateTimeOffset? DecidedAt { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public string? PublishError { get; init; }
}
