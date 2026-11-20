using System.Text.Json;
using SagaFlow.Participants.Messaging;
using Sample.Participants.Payment.Services;

namespace Sample.Participants.Payment.Messaging;

internal sealed class PaymentCommandHandler(PaymentService payments)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ParticipantOutcome> HandleAsync(ParticipantCommand command, CancellationToken cancellationToken)
    {
        Guid orderId;
        string? error;
        switch (command.Name, command.Headers.IsCompensation)
        {
            case ("AuthorizePayment", false):
                var authorize = command.Bind<AuthorizePaymentRequest>();
                orderId = authorize.Id;
                error = await payments.AuthorizeAsync(orderId, authorize.Total, authorize.FailStep, cancellationToken);
                break;
            case ("RefundPayment", true):
                var refund = command.Bind<RefundPaymentRequest>();
                orderId = refund.Id;
                error = await payments.RefundAsync(orderId, cancellationToken);
                break;
            default:
                throw new JsonException("Unsupported payment command type or execution kind.");
        }

        var success = error is null;
        return new ParticipantOutcome
        {
            Name = command.Name + (success ? "Completed" : "Failed"),
            IsSuccess = success,
            Headers = command.Headers,
            Result = JsonSerializer.SerializeToElement(new { orderId, error }, JsonOptions)
        };
    }

    private sealed record AuthorizePaymentRequest(Guid Id, decimal Total, string? FailStep = null);

    private sealed record RefundPaymentRequest(Guid Id);
}
