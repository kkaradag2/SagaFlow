using System.Text.Json;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Messages;
using SagaFlow.Engine.Coordination;
using Sample.DomainStarters.Order.Contracts;
using Sample.DomainStarters.Procurement.Contracts;

namespace Sample.DomainStarters.Procurement.Orchestration;

internal sealed class ProcurementOrderStep(ISagaFlow flow) : ILocalSagaStepHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task ExecuteAsync(SagaCommand command, CancellationToken cancellationToken)
    {
        if (command.Name != "InvokeOrder") return;

        var purchase = command.Payload.Deserialize<PurchasePayload>(Json)
            ?? throw new JsonException("Purchase payload is required.");
        await flow.InvokeAsync("order-process", new
        {
            PurchaseRequestId = purchase.Id,
            Order = new CreateOrderRequest(purchase.CustomerId, purchase.Items, purchase.FailStep)
        }, cancellationToken);
    }
}
