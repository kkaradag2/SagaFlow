using FluentValidation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System.Net;
using System.Net.Http.Json;
using SagaFlow.Core.Abstractions;
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
        Assert.Equal(new[] { "payment", "inventory", "shipment", "notification" }, definition.Steps.Select(step => step.Name));
        Assert.All(definition.Steps, step =>
        {
            Assert.Equal("10s", step.TimeoutLimit);
            Assert.Equal(3, step.MaxAttempts);
            Assert.Contains(step.Transitions, transition => transition.Event == "timeout" && transition.NextStep == "compensation");
        });
    }

    [Fact]
    public async Task PersistsOrderBeforeStartingSaga()
    {
        var repository = new RecordingOrderRepository();
        var saga = new RecordingSagaFlow(() => Assert.True(repository.Added));
        var service = new OrderService(repository, saga);

        var result = await service.CreateAsync(new("customer-1", [new("product-1", 2, 12.50m)], "inventory"));

        Assert.Equal(saga.InstanceId, result.SagaInstanceId);
        Assert.Equal(result.OrderId, repository.Order!.Id);
        Assert.Equal(25m, repository.Order.Total);
        Assert.Equal("inventory", repository.Order.FailureAt);
        Assert.Same(repository.Order, saga.Payload);
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
    }

    private sealed class RecordingOrderService : IOrderService
    {
        public int Calls { get; private set; }

        public Task<CreateOrderResult> CreateAsync(CreateOrderRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new CreateOrderResult(Guid.NewGuid(), Guid.NewGuid()));
        }
    }
}
