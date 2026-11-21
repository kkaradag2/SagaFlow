using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using SagaFlow.Core.Messages;
using SagaFlow.Engine.Messaging;
using Sample.DomainStarters.Order.Contracts;
using Sample.DomainStarters.Order.Data;

namespace Sample.DomainStarters.Order.Services;

internal sealed class IncomingOrderService(PostgreSqlOrderRepository repository, ILogger<IncomingOrderService> logger)
    : IProcessInvocationPolicy, IIncomingInvocationInitializer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly ProcessIdentity Procurement = new("procurement", "procurement-process", "1.0");

    public ValueTask<string?> ValidateAsync(ProcessInvocationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Identity.Source != Procurement || request.Identity.SourceStep != "invokeOrder")
            return ValueTask.FromResult<string?>("Order accepts calls from the Procurement invokeOrder step.");

        try
        {
            var input = Read(request);
            if (input.PurchaseRequestId != Guid.Empty && input.Order is { Items: not null } order &&
                IsValid(order) && order.Items.All(item => item is not null && IsValid(item)))
                return ValueTask.FromResult<string?>(null);
        }
        catch (JsonException)
        {
            // Malformed business payloads are rejected before creating an Order.
        }

        return ValueTask.FromResult<string?>("Order business validation rejected the purchase request.");
    }

    public async ValueTask<JsonElement> InitializeAsync(
        ProcessInvocationRequest request, string targetSagaInstanceId, CancellationToken cancellationToken)
    {
        var input = Read(request);
        var order = OrderCreation.Create(input.Order);
        var orderId = await repository.AddAsync(order, cancellationToken,
            new(Guid.Parse(request.Identity.InvocationId), input.PurchaseRequestId, Guid.Parse(targetSagaInstanceId)));

        logger.LogInformation("Purchase {PurchaseRequestId} initialized order {OrderId} for Saga {SagaInstanceId}",
            input.PurchaseRequestId, orderId, targetSagaInstanceId);
        return JsonSerializer.SerializeToElement(OrderCreation.CreatePayload(order with { Id = orderId }, input.Order.FailStep), Json);
    }

    private static IncomingOrderRequest Read(ProcessInvocationRequest request)
        => request.Payload.Deserialize<IncomingOrderRequest>(Json) ?? throw new JsonException("Order request is required.");

    private static bool IsValid(object value) => Validator.TryValidateObject(value, new(value), [], validateAllProperties: true);

    private sealed record IncomingOrderRequest(Guid PurchaseRequestId, CreateOrderRequest Order);
}
