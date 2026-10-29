using SagaFlow.Core.Abstractions;
using SagaFlow.Order.Domain;
using DomainOrder = SagaFlow.Order.Domain.Order;

namespace SagaFlow.Order.Services;

public sealed class OrderRollbackHandler(IOrderService orders)
    : SagaRollbackHandler<DomainOrder>
{
    public override async Task RollbackAsync(
        DomainOrder payload,
        CancellationToken cancellationToken)
    {
        Console.WriteLine(
            $"[SagaFlow] Order rollback STARTED | OrderId: {payload.Id}");

        await orders.CancelAsync(payload.Id, cancellationToken);

        Console.WriteLine(
            $"[SagaFlow] Order rollback COMPLETED | OrderId: {payload.Id}");
    }
}