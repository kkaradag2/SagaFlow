using Sample.DomainStarters.Procurement.Contracts;

namespace Sample.DomainStarters.Procurement.Services;

public interface IProcurementService
{
    Task<PurchaseCreated> CreateAsync(CreatePurchaseRequest request, CancellationToken cancellationToken);
    Task<PurchaseStatusResponse?> FindAsync(Guid id, CancellationToken cancellationToken);
}
