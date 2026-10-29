using FluentValidation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System.Net;
using System.Net.Http.Json;
using System.Collections.Concurrent;
using System.Text.Json;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Engine.Messaging;
using SagaFlow.Engine.ProcessDefinition;
using SagaFlow.Order.Contracts.Responses;
using SagaFlow.Order.Controllers;
using SagaFlow.Order.Domain;
using SagaFlow.Order.Repositories;
using SagaFlow.Order.Services;
using SagaFlow.Order.Validators;
using DomainOrder = SagaFlow.Order.Domain.Order;

namespace SagaFlow.Samples.Tests;

public sealed class OrderSampleTests
{
    [Fact]
    public async Task ControllerReturnsCreatedForSuccessfulServiceResult()
    {
        var result = new CreateOrderResult(Guid.NewGuid(), Guid.NewGuid());
        var action = await new OrdersController(new StubOrderService(result)).Create(
            new("customer", [new("product", 1, 10m)]), CancellationToken.None);

        var created = Assert.IsType<Microsoft.AspNetCore.Mvc.CreatedResult>(action);
        Assert.Equal($"/orders/{result.OrderId}", created.Location);
        var response = Assert.IsType<SuccessResponse<CreateOrderResult>>(created.Value);
        Assert.Same(result, response.Data);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ControllerDoesNotConvertServiceExceptionsToSuccessOrCustomErrorResults(bool validationError)
    {
        Exception expected = validationError
            ? new ValidationException("invalid order")
            : new InvalidOperationException("No ISagaMessageBus transport adapter is registered.");
        var controller = new OrdersController(new StubOrderService(error: expected));

        var actual = await Assert.ThrowsAsync(expected.GetType(), () => controller.Create(
            new("customer", [new("product", 1, 10m)]), CancellationToken.None));

        Assert.Same(expected, actual);
    }

    [Fact]
    public void OrderYamlUsesCurrentSchemaAndLoadsSuccessfully()
    {
        var definition = new DefinitionYamlCodec().Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "order-process.yaml")));

        Assert.Equal(("order", "order-process", "1.0"), (definition.Domain, definition.Name, definition.Version));
        Assert.Equal(new[] { "payment", "inventory", "managerNotification", "managerApproval", "shipment", "notification" }, definition.Steps.Select(step => step.Name));
        Assert.All(definition.Steps.Where(step => step.Name is "payment" or "inventory" or "shipment" or "notification"), step =>
        {
            Assert.Equal("10s", step.TimeoutLimit);
            Assert.Equal(3, step.MaxAttempts);
            Assert.Contains(step.Transitions, transition => transition.Event == "timeout" && transition.NextStep == "compensation");
        });
        var quantity = definition.Steps.Single(step => step.Name == "inventory").Transitions.First();
        Assert.Equal("context.totalQuantity", quantity.Condition!.Path);
        Assert.Equal("greaterThan", quantity.Condition.Operator);
        Assert.Equal(1000, quantity.Condition.Value.GetInt32());
        Assert.All(definition.Steps.Where(step => step.Name.StartsWith("manager", StringComparison.Ordinal)), step => Assert.Null(step.Compensation));
        Assert.Null(definition.Steps.Single(step => step.Name == "managerApproval").TimeoutLimit);
    }

    [Theory]
    [InlineData(499, 500)]
    [InlineData(500, 500)]
    [InlineData(500, 501)]
    public async Task PersistsOrderBeforeStartingSaga(int firstQuantity, int secondQuantity)
    {
        var repository = new RecordingOrderRepository();
        var saga = new RecordingSagaFlow(() => Assert.True(repository.Added));
        var service = new OrderService(repository, saga);

        var result = await service.CreateAsync(new("customer-1", [new("product-1", firstQuantity, 12.50m), new("product-2", secondQuantity, 1m)], "inventory"));

        Assert.Equal(saga.InstanceId, result.SagaInstanceId);
        Assert.Equal(result.OrderId, repository.Order!.Id);
        Assert.Equal(firstQuantity * 12.50m + secondQuantity, repository.Order.Total);
        Assert.Equal("inventory", repository.Order.FailureAt);
        Assert.Equal((long)firstQuantity + secondQuantity, repository.Order.TotalQuantity);
        Assert.Same(repository.Order, saga.Payload);
    }

    [Fact]
    public async Task OrderRollbackHandlerCancelsThePersistedOrderIdempotently()
    {
        var repository = new InMemoryOrderRepository();
        var service = new OrderService(repository, new RecordingSagaFlow());
        var result = await service.CreateAsync(new("customer-1", [new("product-1", 1, 10m)]));
        var handler = new OrderRollbackHandler(service);
        var order = await repository.FindAsync(result.OrderId);
        Assert.NotNull(order);
        Assert.Equal(OrderStatus.Created, order.Status);

        await handler.RollbackAsync(order, CancellationToken.None);
        await handler.RollbackAsync(order, CancellationToken.None);

        Assert.Equal(OrderStatus.Cancelled, (await repository.FindAsync(result.OrderId))!.Status);
    }

    [Theory]
    [InlineData(999, null, null, false, "Completed", 0)]
    [InlineData(1000, null, null, false, "Completed", 0)]
    [InlineData(1001, true, null, false, "Completed", 0)]
    [InlineData(1001, false, null, false, "Compensated", 2)]
    [InlineData(1001, true, "notification", false, "Compensated", 3)]
    [InlineData(1, null, "notification", false, "Compensated", 3)]
    [InlineData(1, null, "payment", false, "Compensated", 0)]
    [InlineData(1, null, "notification", true, "Failed", 3)]
    public async Task DashboardShowsOrderStarterAndRealRollbackState(int quantity, bool? approved, string? failureAt, bool rollbackFails, string expectedStatus, int compensationCount)
    {
        var bus = new DashboardTestBus();
        await using var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<ISagaMessageBus>();
                services.AddSingleton<ISagaMessageBus>(bus);
                if (rollbackFails)
                {
                    services.RemoveAll<SagaRollbackHandler<DomainOrder>>();
                    services.AddSingleton<SagaRollbackHandler<DomainOrder>>(new FailingOrderRollbackHandler());
                }
            }));
        using var client = factory.CreateClient();
        factory.Services.GetRequiredService<ProcessDefinitionRegistry>().RegisterLocalBatch([
            new DefinitionYamlCodec().Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "order-process.yaml")))]);
        // A supplied totalQuantity is not a request field and must not override the computed item sum.
        var request = new { customerId = "customer", items = new[] { new { productId = "product", quantity, unitPrice = 10m } }, failureAt, totalQuantity = -1 };
        var response = await client.PostAsJsonAsync("/orders", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<SuccessResponse<CreateOrderResult>>())!.Data;
        await using var scope = factory.Services.CreateAsyncScope();
        var receiver = scope.ServiceProvider.GetRequiredService<OutcomeEventReceiver>();
        var dispatched = new List<SagaCommand>();
        while (bus.Commands.TryDequeue(out var command))
        {
            dispatched.Add(command);
            if (command.Headers.StepId == "managerApproval")
            {
                var waiting = (await factory.Services.GetRequiredService<ISagaStateStore>().FindAsync(result.SagaInstanceId))!;
                Assert.Equal(SagaStatus.Running, waiting.Status);
                Assert.Equal("managerApproval", waiting.CurrentStep);
                Assert.Null(waiting.Steps.Single(step => step.StepId == "managerApproval").Result);
                Assert.Null(command.TimeToLive);
                Assert.DoesNotContain(dispatched, item => item.Name == "CreateShipment");
            }
            await receiver.ReceiveAsync(new SagaOutcomeEvent
            {
                Headers = command.Headers,
                IsSuccess = command.Headers.IsCompensation || command.Headers.StepId != failureAt,
                Result = command.Headers.StepId == "managerApproval"
                    ? JsonSerializer.SerializeToElement(new { approved = approved!.Value })
                    : JsonSerializer.SerializeToElement(new { accepted = true })
            });
        }

        var store = factory.Services.GetRequiredService<ISagaStateStore>();
        var before = (await store.FindAsync(result.SagaInstanceId))!;
        Assert.Equal(quantity, before.Context.GetProperty("totalQuantity").GetInt64());
        Assert.False(before.Context.TryGetProperty("deliveryMethod", out _));
        Assert.Equal(typeof(DomainOrder).AssemblyQualifiedName, before.PayloadType);
        var route = quantity <= 1000 ? new[] { "payment", "inventory", "shipment", "notification" }
            : approved == true ? new[] { "payment", "inventory", "managerNotification", "managerApproval", "shipment", "notification" }
            : new[] { "payment", "inventory", "managerNotification", "managerApproval" };
        var executed = failureAt is null ? route : route.Take(Array.IndexOf(route, failureAt) + 1).ToArray();
        Assert.Equal(executed, dispatched.Where(command => !command.Headers.IsCompensation).Select(command => command.Headers.StepId));
        var compensated = compensationCount == 3 ? new[] { "shipment", "inventory", "payment" }
            : compensationCount == 2 ? new[] { "inventory", "payment" } : [];
        Assert.Equal(compensated, dispatched.Where(command => command.Headers.IsCompensation).Select(command => command.Headers.StepId));
        using var detail = JsonDocument.Parse(await client.GetStringAsync($"/sagaflow/api/instances/{result.SagaInstanceId:N}"));
        var root = detail.RootElement;
        Assert.Equal(expectedStatus, root.GetProperty("status").GetString());
        Assert.Equal("order", root.GetProperty("definitionDomain").GetString());
        var starter = root.GetProperty("domainStarter");
        Assert.Equal("CreateOrder", starter.GetProperty("operationName").GetString());
        Assert.Equal("CancelOrder", starter.GetProperty("rollbackOperationName").GetString());
        Assert.Equal("Completed", starter.GetProperty("status").GetString());
        Assert.Equal(result.OrderId, starter.GetProperty("result").GetProperty("id").GetGuid());
        foreach (var property in new[] { "input", "startedAt", "completedAt" })
            Assert.Equal(JsonValueKind.Null, starter.GetProperty(property).ValueKind);
        Assert.Equal(new[] { "payment", "inventory", "managerNotification", "managerApproval", "shipment", "notification" },
            root.GetProperty("steps").EnumerateArray().Select(step => step.GetProperty("stepName").GetString()));
        Assert.Equal(compensationCount, root.GetProperty("compensations").GetArrayLength());
        foreach (var unexecuted in before.Steps.Where(step => step.Status == SagaStepStatus.Pending))
        {
            Assert.Null(unexecuted.ExecutionOrder);
            Assert.Null(unexecuted.Input);
            Assert.Null(unexecuted.Result);
            Assert.Null(unexecuted.StartedAt);
            Assert.Null(unexecuted.CompletedAt);
            Assert.DoesNotContain(before.Compensations, item => item.StepId == unexecuted.StepId);
            var shipmentDto = root.GetProperty("steps").EnumerateArray().Single(step => step.GetProperty("stepName").GetString() == unexecuted.StepId);
            Assert.Equal("Pending", shipmentDto.GetProperty("status").GetString());
            foreach (var property in new[] { "executionOrder", "input", "result", "startedAt", "completedAt" })
                Assert.Equal(JsonValueKind.Null, shipmentDto.GetProperty(property).ValueKind);
        }
        var rollback = root.GetProperty("domainRollback");
        var needsRollback = failureAt is not null || approved == false;
        if (!needsRollback) Assert.Equal(JsonValueKind.Null, rollback.ValueKind);
        else
        {
            Assert.Equal(rollbackFails ? "Failed" : "Completed", rollback.GetProperty("status").GetString());
            Assert.Equal(before.DomainRollback!.StartedAt, rollback.GetProperty("startedAt").GetDateTimeOffset());
            Assert.Equal(before.DomainRollback.CompletedAt, rollback.GetProperty("completedAt").GetDateTimeOffset());
            if (rollbackFails) Assert.Equal("Order cancellation rejected", rollback.GetProperty("error").GetString());
        }
        var order = await factory.Services.GetRequiredService<IOrderRepository>().FindAsync(result.OrderId);
        if (approved.HasValue)
        {
            var decision = before.Steps.Single(step => step.StepId == "managerApproval");
            Assert.Equal(SagaStepStatus.Completed, decision.Status);
            Assert.Equal(approved.Value, decision.Result!.Value.GetProperty("approved").GetBoolean());
        }
        Assert.Equal(needsRollback && !rollbackFails ? OrderStatus.Cancelled : OrderStatus.Created, order!.Status);
        Assert.Equal(before.Revision, (await store.FindAsync(result.SagaInstanceId))!.Revision);
    }

    private sealed class DashboardTestBus : ISagaMessageBus
    {
        public ConcurrentQueue<SagaCommand> Commands { get; } = new();
        public ValueTask PublishAsync(SagaCommand command, CancellationToken cancellationToken = default)
        { Commands.Enqueue(command); return ValueTask.CompletedTask; }
    }

    private sealed class FailingOrderRollbackHandler : SagaRollbackHandler<DomainOrder>
    {
        public override Task RollbackAsync(DomainOrder payload, CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException("Order cancellation rejected"));
    }

    [Fact]
    public async Task BrokerFailureIsPropagatedAndNeverConvertedToFakeSagaSuccess()
    {
        var repository = new InMemoryOrderRepository();
        var service = new OrderService(repository, new FailingSagaFlow());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(new("customer-1", [new("product-1", 1, 10m)])));

        Assert.Contains("No ISagaMessageBus", error.Message);
    }

    public static IEnumerable<object[]> InvalidOrders()
    {
        yield return [new CreateOrderRequest("", [new("product", 1, 1)])];
        yield return [new CreateOrderRequest("customer", [])];
        yield return [new CreateOrderRequest("customer", [null!])];
        yield return [new CreateOrderRequest("customer", [new("", 1, 1)])];
        yield return [new CreateOrderRequest("customer", [new("product", 0, 1)])];
        yield return [new CreateOrderRequest("customer", [new("product", 1, 0)])];
        yield return [new CreateOrderRequest("customer", [new("product", 1, 1)], "billing")];
    }

    [Theory]
    [MemberData(nameof(InvalidOrders))]
    public async Task ValidatorRejectsInvalidOrders(CreateOrderRequest request)
    {
        var result = await new CreateOrderRequestValidator().ValidateAsync(request);

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("payment")]
    [InlineData("inventory")]
    [InlineData("shipment")]
    [InlineData("notification")]
    public async Task ValidatorAcceptsSupportedOptionalFailureTarget(string? failureAt)
    {
        var result = await new CreateOrderRequestValidator().ValidateAsync(
            new CreateOrderRequest("customer", [new("product", 1, 1)], failureAt));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void TotalQuantitySumsAllItemQuantitiesWithoutIntOverflow()
    {
        var order = new DomainOrder(Guid.NewGuid(), "customer", [new("a", int.MaxValue, 1m), new("b", int.MaxValue, 1m)], 0, DateTimeOffset.UtcNow);
        Assert.Equal(2L * int.MaxValue, order.TotalQuantity);
    }

    [Fact]
    public async Task AutomaticValidationRejectsRequestBeforeOrderServiceRuns()
    {
        var service = new RecordingOrderService();
        await using var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IOrderService>();
                services.AddSingleton<IOrderService>(service);
            });
        });
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/orders", new CreateOrderRequest("customer", []));
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(error);
        Assert.Equal("validation_error", error.Code);
        Assert.Equal("Request validation failed.", error.Message);
        Assert.Contains(error.Errors, detail =>
            detail.Field == "items" && detail.Message == "At least one order item is required.");
        Assert.False(string.IsNullOrWhiteSpace(error.TraceId));
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task AutomaticValidationRejectsUnsupportedFailureTarget()
    {
        var service = new RecordingOrderService();
        await using var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IOrderService>();
                services.AddSingleton<IOrderService>(service);
            });
        });
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/orders",
            new CreateOrderRequest("customer", [new("product", 1, 10m)], "billing"));
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(error!.Errors, detail => detail.Field == "failureAt");
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task SuccessfulRequestReturnsCreatedSuccessResponse()
    {
        var result = new CreateOrderResult(Guid.NewGuid(), Guid.NewGuid());
        await using var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IOrderService>();
                services.AddSingleton<IOrderService>(new StubOrderService(result));
            });
        });
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/orders",
            new CreateOrderRequest("customer", [new("product", 1, 10m)]));
        var success = await response.Content.ReadFromJsonAsync<SuccessResponse<CreateOrderResult>>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/orders/{result.OrderId}", response.Headers.Location?.OriginalString);
        Assert.NotNull(success);
        Assert.Equal(result, success.Data);
    }

    [Fact]
    public async Task SwaggerDocumentIsAvailableInDevelopment()
    {
        await using var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/swagger/v1/swagger.json");
        var document = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"/orders\"", document);
    }

    private sealed class RecordingOrderRepository : IOrderRepository
    {
        public bool Added { get; private set; }
        public DomainOrder? Order { get; private set; }
        public ValueTask AddAsync(DomainOrder order, CancellationToken cancellationToken = default)
        { Order = order; Added = true; return ValueTask.CompletedTask; }
        public ValueTask<DomainOrder?> FindAsync(Guid id, CancellationToken cancellationToken = default) => ValueTask.FromResult(Order);
        public ValueTask CancelAsync(Guid id, CancellationToken cancellationToken = default)
        { if (Order is null) throw new KeyNotFoundException(); Order = Order with { Status = OrderStatus.Cancelled }; return ValueTask.CompletedTask; }
    }

    private sealed class RecordingSagaFlow(Action? onStart = null) : ISagaFlow
    {
        public Guid InstanceId { get; } = Guid.NewGuid();
        public object? Payload { get; private set; }
        public int Calls { get; private set; }
        public Task<Guid> StartAsync<TPayload>(TPayload payload, CancellationToken cancellationToken = default)
        { onStart?.Invoke(); Calls++; Payload = payload; return Task.FromResult(InstanceId); }
    }

    private sealed class FailingSagaFlow : ISagaFlow
    {
        public Task<Guid> StartAsync<TPayload>(TPayload payload, CancellationToken cancellationToken = default) =>
            Task.FromException<Guid>(new InvalidOperationException("No ISagaMessageBus transport adapter is registered."));
    }

    private sealed class StubOrderService(CreateOrderResult? result = null, Exception? error = null) : IOrderService
    {
        public Task<CreateOrderResult> CreateAsync(CreateOrderRequest request, CancellationToken cancellationToken = default) =>
            error is null ? Task.FromResult(result!) : Task.FromException<CreateOrderResult>(error);
        public Task CancelAsync(Guid orderId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingOrderService : IOrderService
    {
        public int Calls { get; private set; }

        public Task<CreateOrderResult> CreateAsync(CreateOrderRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new CreateOrderResult(Guid.NewGuid(), Guid.NewGuid()));
        }
        public Task CancelAsync(Guid orderId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
