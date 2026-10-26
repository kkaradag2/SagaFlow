using SagaFlow.Shipment.Domain;
using SagaFlow.Shipment.Repositories;

namespace SagaFlow.Shipment.Services;

public sealed class ShipmentService(IShipmentRepository repository) : IShipmentService
{
    public ShipmentRecord CreateShipment(string orderId, string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        var normalizedAddress = address.Trim();
        return repository.Upsert(orderId, current => current switch
        {
            null => new(orderId, normalizedAddress, $"SHP-{orderId.ToUpperInvariant()}", ShipmentStatus.Created),
            { Status: ShipmentStatus.Cancelled } => throw new InvalidOperationException("A cancelled shipment cannot be created again."),
            _ when current.Address != normalizedAddress => throw new InvalidOperationException("A shipment with a different address already exists."),
            _ => current
        });
    }

    public ShipmentRecord CancelShipment(string orderId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);
        return repository.Upsert(orderId, current => current switch
        {
            null => throw new InvalidOperationException("Shipment was not found."),
            { Status: ShipmentStatus.Cancelled } => current,
            _ => current with { Status = ShipmentStatus.Cancelled }
        });
    }
}
