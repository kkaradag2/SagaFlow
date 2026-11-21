using Microsoft.Extensions.Logging;
using Sample.Participants.Shipment.Data;

namespace Sample.Participants.Shipment.Services;

internal sealed class ShipmentService(ShipmentRepository repository, ILogger<ShipmentService> logger)
{
    public async Task<string?> CreateAsync(Guid orderId, string? failStep, CancellationToken cancellationToken)
    {
        if (orderId == Guid.Empty)
            return "invalid_shipment_request";

        if (string.Equals(failStep?.Trim(), "shipment", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Shipment failure requested for order {OrderId}", orderId);
            return "shipment_creation_failed";
        }

        var trackingNumber = $"SHP-{orderId:N}";
        await repository.CreateAsync(orderId, trackingNumber, cancellationToken);
        logger.LogInformation("Shipment creation confirmed for order {OrderId}, tracking {TrackingNumber}",
            orderId, trackingNumber);
        return null;
    }

    public async Task<string?> CancelAsync(Guid orderId, CancellationToken cancellationToken)
    {
        if (orderId == Guid.Empty)
            return "invalid_shipment_request";

        if (!await repository.CancelAsync(orderId, cancellationToken))
            return "shipment_not_found";

        logger.LogInformation("Shipment cancellation confirmed for order {OrderId}", orderId);
        return null;
    }
}
