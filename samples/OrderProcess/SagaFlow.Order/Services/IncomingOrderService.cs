using System.Text.Json;
using FluentValidation;
using SagaFlow.Core.Messages;
using SagaFlow.Core.Abstractions;
using SagaFlow.Engine.Messaging;
using SagaFlow.Order.Domain;
using SagaFlow.Order.Repositories;
using SagaFlow.Order.Validators;
using DomainOrder = SagaFlow.Order.Domain.Order;

namespace SagaFlow.Order.Services;

public sealed record IncomingOrderRequest(Guid PurchaseRequestId, CreateOrderRequest Order);
public sealed record IncomingOrderLink(Guid PurchaseRequestId, string InvocationId, string TargetSagaInstanceId, DomainOrder Order);

/// <summary>Order-owned admission and volatile business idempotency. Not a cross-store transaction/inbox.</summary>
public sealed class IncomingOrderService(IOrderRepository orders, TimeProvider time) : IProcessInvocationPolicy, IIncomingInvocationInitializer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, (ProcessInvocationIdentity Identity, string Payload, IncomingOrderLink Link)> links = new(StringComparer.Ordinal);
    private static IncomingOrderRequest Read(ProcessInvocationRequest request)
    {
        if (request.Identity.Source != new ProcessIdentity("procurement", "procurement-process", "1.0"))
            throw new ValidationException("Order accepts procurement-process@1.0 requests from the procurement domain.");
        var value = request.Payload.Deserialize<IncomingOrderRequest>(Json) ?? throw new ValidationException("Order request is required.");
        if (value.PurchaseRequestId == Guid.Empty || value.Order is null) throw new ValidationException("PurchaseRequestId and order are required.");
        new CreateOrderRequestValidator().ValidateAndThrow(value.Order);
        try { _ = value.Order.Items.Sum(item => item.Quantity * item.UnitPrice); }
        catch (OverflowException) { throw new ValidationException("Order total exceeds the supported amount range."); }
        return value;
    }
    public ValueTask<string?> ValidateAsync(ProcessInvocationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { Read(request); return ValueTask.FromResult<string?>(null); }
        catch (Exception error) when (error is JsonException or ValidationException or NotSupportedException)
        { return ValueTask.FromResult<string?>("Order business validation rejected the request."); }
    }
    public async ValueTask<JsonElement> InitializeAsync(ProcessInvocationRequest request, string targetSagaInstanceId, CancellationToken cancellationToken)
    {
        var value = Read(request);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (links.TryGetValue(request.Identity.InvocationId, out var existing))
            {
                if (existing.Identity != request.Identity || existing.Payload != request.Payload.GetRawText()
                    || existing.Link.TargetSagaInstanceId != targetSagaInstanceId) throw new InvalidOperationException("Incoming Order identity/payload conflict.");
                return JsonSerializer.SerializeToElement(existing.Link.Order, Json);
            }
            var order = OrderCreation.Create(value.Order, time.GetUtcNow());
            await orders.AddAsync(order, cancellationToken);
            var link = new IncomingOrderLink(value.PurchaseRequestId, request.Identity.InvocationId, targetSagaInstanceId, order);
            links.Add(request.Identity.InvocationId, (request.Identity, request.Payload.GetRawText(), link));
            return JsonSerializer.SerializeToElement(order, Json);
        }
        finally { gate.Release(); }
    }
    public async Task<IncomingOrderLink?> FindAsync(string invocationId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var link = links.GetValueOrDefault(invocationId).Link;
            if (link is null) return null;
            var current = await orders.FindAsync(link.Order.Id, cancellationToken);
            return current is null ? null : link with { Order = current };
        }
        finally { gate.Release(); }
    }
}

/// <summary>Incoming execution contexts are JSON; reuse the same Order cancellation service.</summary>
public sealed class IncomingOrderRollbackHandler(IOrderService orders) : SagaRollbackHandler<JsonElement>
{
    public override Task RollbackAsync(JsonElement payload, CancellationToken cancellationToken) =>
        orders.CancelAsync(payload.GetProperty("id").GetGuid(), cancellationToken);
}
