using SagaFlow.Shipment.Domain;

namespace SagaFlow.Shipment.Services;

public interface IShipmentService
{
    ShipmentRecord CreateShipment(string orderId, string address);
    ShipmentRecord CancelShipment(string orderId);
}
