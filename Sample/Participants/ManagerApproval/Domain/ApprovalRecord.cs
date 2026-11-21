namespace Sample.Participants.ManagerApproval.Domain;

public sealed record ApprovalRecord(
    Guid ApprovalId,
    Guid OrderId,
    int TotalQuantity,
    DateTimeOffset RequestedAt,
    bool? Approved,
    DateTimeOffset? DecidedAt,
    DateTimeOffset? PublishedAt)
{
    public string Status => (Approved, PublishedAt) switch
    {
        (null, _) => "Pending",
        (_, null) => "PendingDelivery",
        (true, _) => "Approved",
        _ => "Rejected"
    };
}
