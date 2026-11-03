using System.Text.Json;
using SagaFlow.Core.Messages;
using SagaFlow.Engine.ProcessDefinition;
using SagaFlow.Inventory.Domain;
using SagaFlow.Inventory.Repositories;
using SagaFlow.Inventory.Services;
using SagaFlow.Notification.Repositories;
using SagaFlow.Notification.Services;
using SagaFlow.Payment.Repositories;
using SagaFlow.Payment.Services;
using SagaFlow.Warehouse;
using Microsoft.Extensions.Logging.Abstractions;

namespace SagaFlow.Samples.Tests;

public sealed class ReturnDomainTests
{
    [Fact]
    public async Task ReturnYamlLoadsAndContainsBothBusinessBranches()
    {
        var definition = await new YamlSagaDefinitionLoader(AppContext.BaseDirectory).LoadAsync("return-process.yaml");
        Assert.Equal("return", definition.Domain);
        Assert.Equal("return-process", definition.Name);
        Assert.Equal(5, definition.Steps.Count);
        var warehouse = definition.Steps.Single(x => x.Name == "warehouse");
        Assert.Contains(warehouse.Transitions, x => x.NextStep == "payment");
        Assert.Contains(warehouse.Transitions, x => x.NextStep == "rejectedNotification");
        Assert.All(warehouse.Transitions.Where(x => x.Event == "success"), x => Assert.NotNull(x.Condition));
    }

    [Fact]
    public void ReturnRefundIsIdempotentAndDistinctFromOrderCompensation()
    {
        var repo = new InMemoryPaymentRepository();
        var service = new PaymentService(repo);
        var first = service.RefundReturnPayment("return-1", "order-1", 10m);
        Assert.Same(first, service.RefundReturnPayment("return-1", "order-1", 10m));
        Assert.Throws<InvalidOperationException>(() => service.RefundReturnPayment("return-1", "order-2", 10m));
        Assert.Null(repo.Find("order-1"));
    }

    [Fact]
    public void ReturnRestockAndNotificationsAreIdempotent()
    {
        var inventory = new InventoryService(new InMemoryInventoryRepository());
        var lines = new[] { new InventoryLine("sku", 2) };
        var first = inventory.RestockReturn("return-1", "order-1", lines);
        Assert.Same(first, inventory.RestockReturn("return-1", "order-1", lines));
        Assert.Throws<InvalidOperationException>(() => inventory.RestockReturn("return-1", "order-1", [new("other", 2)]));
        var notifications = new NotificationService(new InMemoryNotificationRepository());
        Assert.Same(notifications.NotifyReturn("r1", "o1", "customer", true),
            notifications.NotifyReturn("r1", "o1", "customer", true));
        Assert.Equal("Rejected", notifications.NotifyReturn("r2", "o2", "customer", false).Decision);
    }

    [Fact]
    public void WarehouseDefersOutcomeAndRejectsConflictingOrDuplicateCommands()
    {
        var repository = new WarehouseRepository();
        var handler = new WarehouseCommandHandler(repository, TimeProvider.System);
        var returnId = Guid.NewGuid();
        var command = new SagaCommand {
            Name = "InspectReturn", Payload = JsonSerializer.SerializeToElement(new { returnId, orderId = Guid.NewGuid() }),
            Headers = new() { SagaInstanceId = "saga-1", CorrelationId = "correlation-1", StepId = "warehouse" }
        };
        Assert.Null(handler.Handle(command, "return.outcomes"));
        Assert.Null(handler.Handle(command, "return.outcomes"));
        Assert.Single(repository.Pending());
        Assert.Throws<InvalidOperationException>(() => handler.Handle(command, "other.outcomes"));
        Assert.Throws<InvalidOperationException>(() => handler.Handle(new SagaCommand {
            Name = command.Name, Payload = command.Payload,
            Headers = new() { SagaInstanceId = "saga-2", CorrelationId = "correlation-2", StepId = "warehouse" }
        }, "return.outcomes"));
        var claim = repository.BeginDecision(returnId, true, DateTimeOffset.UtcNow);
        Assert.Throws<InvalidOperationException>(() => repository.BeginDecision(returnId, false, DateTimeOffset.UtcNow));
        repository.Complete(claim, null, false, DateTimeOffset.UtcNow);
        Assert.Throws<InvalidOperationException>(() => repository.BeginDecision(returnId, true, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ConcurrentWarehouseDecisionsHaveOneWinner()
    {
        var repository = new WarehouseRepository();
        var returnId = Guid.NewGuid();
        repository.Register(new InspectionRecord(returnId, Guid.NewGuid(), "saga", "corr", "warehouse",
            "return.outcomes", DateTimeOffset.UtcNow));
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            try { repository.BeginDecision(returnId, true, DateTimeOffset.UtcNow); return true; }
            catch (InvalidOperationException) { return false; }
        })));
        Assert.Single(results, x => x);
        Assert.Equal(InspectionStatus.Publishing, repository.Find(returnId)!.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WarehousePublicationFailureRetainsDecisionAndVisibleState(bool definitelyNotSent)
    {
        var repository = new WarehouseRepository();
        var returnId = Guid.NewGuid();
        repository.Register(new InspectionRecord(returnId, Guid.NewGuid(), "saga", "corr", "warehouse",
            "return.outcomes", DateTimeOffset.UtcNow));
        var publisher = new FailingPublisher(definitelyNotSent);
        var service = new WarehouseDecisionService(repository, publisher, TimeProvider.System,
            NullLogger<WarehouseDecisionService>.Instance);

        var failed = await service.DecideAsync(returnId, true, CancellationToken.None);
        Assert.False(failed.Published);
        Assert.Equal(definitelyNotSent ? InspectionStatus.PublishNotSent : InspectionStatus.PublishFailed,
            repository.Find(returnId)!.Status);
        Assert.True(repository.Find(returnId)!.Accepted);
        Assert.NotNull(repository.Find(returnId)!.PublishError);
        Assert.Throws<InvalidOperationException>(() => repository.BeginDecision(returnId, false, DateTimeOffset.UtcNow));

        if (definitelyNotSent)
        {
            var retried = await service.DecideAsync(returnId, true, CancellationToken.None);
            Assert.False(retried.Published);
            Assert.Equal(2, publisher.Attempts);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.DecideAsync(returnId, true, CancellationToken.None));
            Assert.Equal(1, publisher.Attempts);
        }
    }

    private sealed class FailingPublisher(bool definitelyNotSent) : IWarehouseOutcomePublisher
    {
        public int Attempts { get; private set; }
        public Task PublishAsync(SagaOutcomeEvent outcome, string replyTo)
        {
            Attempts++;
            if (definitelyNotSent)
                throw new WarehouseOutcomeNotSentException(new IOException("Connection unavailable."));
            throw new IOException("Publisher confirmation unavailable.");
        }
    }
}
