using SagaFlow.Shipment.Domain;

namespace SagaFlow.Shipment.Repositories;

public interface IShipmentRepository
{
    ShipmentRecord? Find(string orderId);
    ShipmentRecord Upsert(string orderId, Func<ShipmentRecord?, ShipmentRecord> update);
}
