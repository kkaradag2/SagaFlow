namespace SagaFlow.Warehouse;

public enum InspectionStatus { Pending, Publishing, Accepted, Rejected, PublishNotSent, PublishFailed }

public sealed record InspectionRecord(Guid ReturnId, Guid OrderId, DateTimeOffset RequestedAt)
{
    public Guid InspectionId { get; init; } = Guid.NewGuid();
    public InspectionStatus Status { get; init; } = InspectionStatus.Pending;
    public bool? Accepted { get; init; }
    public DateTimeOffset? DecidedAt { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public string? PublishError { get; init; }
}

public sealed class WarehouseRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, InspectionRecord> _records = [];

    public InspectionRecord Register(InspectionRecord record)
    {
        lock (_gate)
        {
            var key = record.InspectionId;
            if (_records.TryGetValue(key, out var current))
            {
                if (current.ReturnId != record.ReturnId || current.OrderId != record.OrderId)
                    throw new InvalidOperationException("Inspection business ID has conflicting input.");
                return current;
            }
            if (_records.Values.Any(x => x.ReturnId == record.ReturnId))
                throw new InvalidOperationException("Return ID is already linked to another Saga inspection.");
            _records.Add(key, record);
            return record;
        }
    }

    public IReadOnlyList<InspectionRecord> Pending()
    {
        lock (_gate) return _records.Values.Where(x => x.Status == InspectionStatus.Pending).OrderBy(x => x.RequestedAt).ToArray();
    }

    public InspectionRecord? Find(Guid returnId)
    {
        lock (_gate) return _records.Values.SingleOrDefault(x => x.ReturnId == returnId);
    }

    public InspectionRecord BeginDecision(Guid returnId, bool accepted, DateTimeOffset now)
    {
        lock (_gate)
        {
            var current = _records.Values.SingleOrDefault(x => x.ReturnId == returnId)
                ?? throw new KeyNotFoundException("Inspection was not found.");
            if (current.Status == InspectionStatus.PublishNotSent && current.Accepted != accepted)
                throw new InvalidOperationException("A recorded decision cannot be changed.");
            if (current.Status is not (InspectionStatus.Pending or InspectionStatus.PublishNotSent))
                throw new InvalidOperationException("Inspection decision is already in progress or completed.");
            var claim = current with { Status = InspectionStatus.Publishing, Accepted = accepted,
                DecidedAt = current.DecidedAt ?? now, PublishError = null };
            _records[current.InspectionId] = claim;
            return claim;
        }
    }

    public InspectionRecord Complete(InspectionRecord claim, string? error, bool definitelyNotSent, DateTimeOffset now)
    {
        lock (_gate)
        {
            var key = claim.InspectionId;
            var current = _records[key];
            if (current.Status != InspectionStatus.Publishing || current.Accepted != claim.Accepted)
                throw new InvalidOperationException("Inspection publication is not active.");
            var completed = current with {
                Status = error is null ? (claim.Accepted == true ? InspectionStatus.Accepted : InspectionStatus.Rejected)
                    : definitelyNotSent ? InspectionStatus.PublishNotSent : InspectionStatus.PublishFailed,
                PublishedAt = error is null ? now : null, PublishError = error };
            _records[key] = completed;
            return completed;
        }
    }
}
