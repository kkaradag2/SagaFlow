using Microsoft.Extensions.Logging;
using Sample.Participants.Payment.Data;

namespace Sample.Participants.Payment.Services;

internal sealed class PaymentService(PaymentRepository repository, ILogger<PaymentService> logger)
{
    public async Task<string?> AuthorizeAsync(
        Guid orderId, decimal amount, string? failStep, CancellationToken cancellationToken)
    {
        if (orderId == Guid.Empty || amount <= 0 || amount > 9999999999999999.99m || decimal.Round(amount, 2) != amount)
            return "invalid_payment";

        if (string.Equals(failStep?.Trim(), "payment", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Payment failure requested for order {OrderId}", orderId);
            return "payment_declined";
        }

        if (!await repository.AuthorizeAsync(orderId, amount, cancellationToken))
            return "payment_amount_conflict";

        logger.LogInformation("Payment authorization confirmed for order {OrderId}", orderId);
        return null;
    }

    public async Task<string?> RefundAsync(Guid orderId, CancellationToken cancellationToken)
    {
        if (orderId == Guid.Empty)
            return "invalid_payment";

        if (!await repository.RefundAsync(orderId, cancellationToken))
            return "payment_not_found";

        logger.LogInformation("Payment cancellation confirmed for order {OrderId}", orderId);
        return null;
    }
}
