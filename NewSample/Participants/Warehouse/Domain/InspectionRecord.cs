namespace Sample.Participants.Warehouse.Domain;

public sealed record InspectionRecord(
    Guid InspectionId,
    Guid ReturnId,
    Guid OrderId,
    DateTimeOffset RequestedAt,
    bool? Accepted,
    DateTimeOffset? DecidedAt,
    DateTimeOffset? PublishedAt)
{
    public string Status => (Accepted, PublishedAt) switch
    {
        (null, _) => "Pending",
        (_, null) => "PendingDelivery",
        (true, _) => "Accepted",
        _ => "Rejected"
    };
}
