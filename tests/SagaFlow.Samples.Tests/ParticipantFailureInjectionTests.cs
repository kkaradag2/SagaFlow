using System.Text.Json;
using SagaFlow.Core.Messages;
using SagaFlow.Inventory.Messaging;
using SagaFlow.Inventory.Repositories;
using SagaFlow.Inventory.Services;
using SagaFlow.Notification.Messaging;
using SagaFlow.Notification.Repositories;
using SagaFlow.Notification.Services;
using SagaFlow.Payment.Messaging;
using SagaFlow.Payment.Repositories;
using SagaFlow.Payment.Services;
using SagaFlow.Shipment.Messaging;
using SagaFlow.Shipment.Repositories;
using SagaFlow.Shipment.Services;

namespace SagaFlow.Samples.Tests;

public sealed class ParticipantFailureInjectionTests
{
    [Fact]
    public void ManagerNotificationUsesExistingParticipantWithoutOverwritingFinalOrderNotification()
    {
        var repository = new InMemoryNotificationRepository();
        var handler = new NotificationCommandHandler(new NotificationService(repository));
        var command = new SagaCommand
        {
            Name = "NotifyManager", Headers = new() { SagaInstanceId = "saga", CorrelationId = "correlation", StepId = "managerNotification" },
            Payload = JsonSerializer.SerializeToElement(new { id = Guid.ParseExact(OrderId, "N"), totalQuantity = 1001 })
        };
        var result = handler.Handle(command);
        var repeated = handler.Handle(command);
        Assert.True(result.IsSuccess);
        Assert.True(result.ToOutcome(command).IsSuccess);
        Assert.Equal(1001, repository.FindManager(OrderId)!.TotalQuantity);
        Assert.Contains(OrderId, repository.FindManager(OrderId)!.Message);
        Assert.Same(result.Value, repeated.Value);
        Assert.Null(repository.Find(OrderId));
        var final = handler.Handle(new SagaCommand
        {
            Name = "SendOrderNotification", Headers = command.Headers,
            Payload = JsonSerializer.SerializeToElement(new { id = Guid.ParseExact(OrderId, "N"), customerId = "customer" })
        });
        Assert.True(final.IsSuccess);
        Assert.NotNull(repository.Find(OrderId));
    }
    [Fact]
    public void PaymentFailureDoesNotAuthorizePayment()
    {
        var repository = new InMemoryPaymentRepository();
        var command = Command("AuthorizePayment", "payment");
        var result = new PaymentCommandHandler(new PaymentService(repository)).Handle(command);
        var outcome = result.ToOutcome(command);

        Assert.False(result.IsSuccess);
        Assert.Null(repository.Find(OrderId));
        Assert.False(outcome.IsSuccess);
        Assert.Equal("AuthorizePaymentFailed", outcome.Name);
        Assert.Same(command.Headers, outcome.Headers);
        Assert.Equal("Sample failure requested at payment.", outcome.Result.GetProperty("error").GetString());
    }

    [Fact]
    public void InventoryFailureDoesNotReserveInventory()
    {
        var repository = new InMemoryInventoryRepository();
        var result = new InventoryCommandHandler(new InventoryService(repository))
            .Handle(Command("ReserveInventory", "inventory"));

        Assert.False(result.IsSuccess);
        Assert.Null(repository.Find(OrderId));
    }

    [Fact]
    public void ShipmentFailureDoesNotCreateShipment()
    {
        var repository = new InMemoryShipmentRepository();
        var result = new ShipmentCommandHandler(new ShipmentService(repository))
            .Handle(Command("CreateShipment", "shipment"));

        Assert.False(result.IsSuccess);
        Assert.Null(repository.Find(OrderId));
    }

    [Fact]
    public void NotificationFailureDoesNotSendNotification()
    {
        var repository = new InMemoryNotificationRepository();
        var result = new NotificationCommandHandler(new NotificationService(repository))
            .Handle(Command("SendOrderNotification", "notification"));

        Assert.False(result.IsSuccess);
        Assert.Null(repository.Find(OrderId));
    }

    private const string OrderId = "11111111111111111111111111111111";

    private static SagaCommand Command(string name, string failureAt) => new()
    {
        Name = name,
        Headers = new()
        {
            SagaInstanceId = "22222222222222222222222222222222",
            CorrelationId = "33333333333333333333333333333333",
            StepId = failureAt
        },
        Payload = JsonSerializer.SerializeToElement(new
        {
            id = Guid.ParseExact(OrderId, "N"),
            customerId = "customer-1",
            total = 25m,
            items = new[] { new { productId = "product-1", quantity = 2 } },
            failureAt
        })
    };
}
