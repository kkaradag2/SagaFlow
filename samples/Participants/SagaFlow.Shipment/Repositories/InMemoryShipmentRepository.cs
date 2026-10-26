using System.Collections.Concurrent;
using SagaFlow.Shipment.Domain;

namespace SagaFlow.Shipment.Repositories;

public sealed class InMemoryShipmentRepository : IShipmentRepository
{
    private readonly ConcurrentDictionary<string, ShipmentRecord> _shipments = new(StringComparer.Ordinal);
    public ShipmentRecord? Find(string orderId) => _shipments.GetValueOrDefault(orderId);
    public ShipmentRecord Upsert(string orderId, Func<ShipmentRecord?, ShipmentRecord> update) =>
        _shipments.AddOrUpdate(orderId, _ => update(null), (_, current) => update(current));
}
