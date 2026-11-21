using Sample.Participants.Warehouse.Data;
using Sample.Participants.Warehouse.Domain;

namespace Sample.Participants.Warehouse.Services;

internal sealed class InspectionService(InspectionRepository inspections, InspectionOutbox outbox) : IInspectionService
{
    public Task<IReadOnlyList<InspectionRecord>> GetPendingAsync(CancellationToken cancellationToken)
        => inspections.GetPendingAsync(cancellationToken);

    public Task<InspectionRecord?> FindAsync(Guid returnId, CancellationToken cancellationToken)
        => inspections.FindAsync(returnId, cancellationToken);

    public async Task<InspectionRecord?> DecideAsync(Guid returnId, bool accepted, CancellationToken cancellationToken)
    {
        var record = await inspections.DecideAsync(returnId, accepted, cancellationToken);
        if (record?.Accepted != accepted || record.PublishedAt is not null)
            return record;

        await outbox.TryPublishAsync(returnId, cancellationToken);
        return await inspections.FindAsync(returnId, cancellationToken);
    }
}
