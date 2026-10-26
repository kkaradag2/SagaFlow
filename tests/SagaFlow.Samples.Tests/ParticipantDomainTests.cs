using SagaFlow.Inventory.Domain;
using SagaFlow.Inventory.Repositories;
using SagaFlow.Inventory.Services;
using SagaFlow.Notification.Repositories;
using SagaFlow.Notification.Services;
using SagaFlow.Payment.Domain;
using SagaFlow.Payment.Repositories;
using SagaFlow.Payment.Services;
using SagaFlow.Shipment.Domain;
using SagaFlow.Shipment.Repositories;
using SagaFlow.Shipment.Services;

namespace SagaFlow.Samples.Tests;

public sealed class ParticipantDomainTests
{
    [Fact]
    public void PaymentAuthorizationAndRefundAreDeterministic()
    {
        var repository = new InMemoryPaymentRepository();
        var service = new PaymentService(repository);
        var first = service.AuthorizePayment("order-1", 25m);
        Assert.Same(first, service.AuthorizePayment("order-1", 25m));
        Assert.Equal(PaymentStatus.Refunded, service.RefundPayment("order-1").Status);
        Assert.Equal(PaymentStatus.Refunded, repository.Find("order-1")!.Status);
    }

    [Fact]
    public void InventoryReservationAndReleaseAreDeterministic()
    {
        var repository = new InMemoryInventoryRepository();
        var service = new InventoryService(repository);
        var lines = new[] { new InventoryLine("product-1", 2) };
        var first = service.ReserveInventory("order-1", lines);
        Assert.Same(first, service.ReserveInventory("order-1", lines));
        Assert.Equal(ReservationStatus.Released, service.ReleaseInventory("order-1").Status);
    }

    [Fact]
    public void ShipmentCreationAndCancellationAreDeterministic()
    {
        var repository = new InMemoryShipmentRepository();
        var service = new ShipmentService(repository);
        var first = service.CreateShipment("order-1", "Istanbul");
        Assert.Equal("SHP-ORDER-1", first.TrackingNumber);
        Assert.Same(first, service.CreateShipment("order-1", "Istanbul"));
        Assert.Equal(ShipmentStatus.Cancelled, service.CancelShipment("order-1").Status);
    }

    [Fact]
    public void NotificationSendIsDeterministicAndHasNoCompensationOperation()
    {
        var repository = new InMemoryNotificationRepository();
        var service = new NotificationService(repository);
        var first = service.SendOrderNotification("order-1", "customer@example.test");
        Assert.Same(first, service.SendOrderNotification("order-1", "customer@example.test"));
        Assert.Equal("Order order-1 has been completed.", first.Message);
    }
}
