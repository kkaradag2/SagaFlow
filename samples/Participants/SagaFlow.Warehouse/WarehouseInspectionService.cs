namespace SagaFlow.Warehouse;

public sealed class WarehouseInspectionService(WarehouseRepository repository, WarehouseDecisionService decisions)
{
    public IReadOnlyList<InspectionRecord> Pending() => repository.Pending();

    public InspectionRecord? Find(Guid returnId) => repository.Find(returnId);

    public Task<WarehouseDecisionResult> DecideAsync(Guid returnId, bool accepted, CancellationToken cancellationToken) =>
        decisions.DecideAsync(returnId, accepted, cancellationToken);
}
