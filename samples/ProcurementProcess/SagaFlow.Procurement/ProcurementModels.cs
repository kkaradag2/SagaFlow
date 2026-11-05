using System.Collections.Concurrent;

namespace SagaFlow.Procurement;

public sealed record PurchaseItem(string ProductId, int Quantity, decimal UnitPrice);
public sealed record CreatePurchaseRequest(string EmployeeId, string CustomerId, IReadOnlyList<PurchaseItem> Items, string? FailureAt = null)
{
    public bool IsValid()
    {
        if (string.IsNullOrWhiteSpace(EmployeeId) || string.IsNullOrWhiteSpace(CustomerId) || Items is not { Count: > 0 }
            || Items.Any(i => i is null || string.IsNullOrWhiteSpace(i.ProductId) || i.Quantity <= 0 || i.UnitPrice <= 0)
            || FailureAt is not (null or "payment" or "inventory" or "shipment" or "notification")) return false;
        try { _ = Items.Sum(i => i.Quantity * i.UnitPrice); return true; }
        catch (OverflowException) { return false; }
    }
}
// id/totalQuantity reuse the existing ManagerApproval wire contract. id is a PurchaseRequestId here, NOT an OrderId.
public sealed record PurchasePayload(Guid Id, string EmployeeId, string CustomerId, IReadOnlyList<PurchaseItem> Items, string? FailureAt)
{
    public long TotalQuantity => Items.Sum(i => (long)i.Quantity);
}
public sealed record PurchaseRecord(PurchasePayload Request, DateTimeOffset CreatedAt, Guid? SagaInstanceId = null, bool StartFailed = false);
public sealed record PurchaseCreated(Guid PurchaseRequestId, Guid SagaInstanceId);
public sealed record PurchaseView(Guid PurchaseRequestId, Guid? SagaInstanceId, string EmployeeId, string Status,
    string? SagaStatus, string? SourceCorrelationId, string? InvocationId, string? OrderSagaInstanceId, string? LastKnownOrderStatus);
public sealed class PurchaseRepository
{
    private readonly ConcurrentDictionary<Guid, PurchaseRecord> records = new();
    public void Add(PurchaseRecord record)
    { if (!records.TryAdd(record.Request.Id, record)) throw new InvalidOperationException("Purchase request already exists."); }
    public PurchaseRecord? Find(Guid id) => records.GetValueOrDefault(id);
    public void Attach(Guid id, Guid? sagaId, bool failed = false)
    {
        while (true)
        {
            var record = records[id];
            if (record.SagaInstanceId is not null && record.SagaInstanceId != sagaId) throw new InvalidOperationException("Purchase Saga identity conflict.");
            if (records.TryUpdate(id, record with { SagaInstanceId = sagaId, StartFailed = failed }, record)) return;
        }
    }
}
public sealed class PurchaseStartException(Guid purchaseRequestId, Exception inner)
    : InvalidOperationException("Purchase request persisted, but local orchestration could not start.", inner)
{ public Guid PurchaseRequestId { get; } = purchaseRequestId; }
