using Sample.Participants.Warehouse.Domain;

namespace Sample.Participants.Warehouse.Services;

public interface IInspectionService
{
    Task<IReadOnlyList<InspectionRecord>> GetPendingAsync(CancellationToken cancellationToken);

    Task<InspectionRecord?> FindAsync(Guid returnId, CancellationToken cancellationToken);

    Task<InspectionRecord?> DecideAsync(Guid returnId, bool accepted, CancellationToken cancellationToken);
}
