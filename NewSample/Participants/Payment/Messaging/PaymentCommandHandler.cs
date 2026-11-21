using System.Text.Json;
using SagaFlow.Participants.Messaging;
using Sample.Participants.Messaging;
using Sample.Participants.Payment.Services;

namespace Sample.Participants.Payment.Messaging;

internal sealed class PaymentCommandHandler(PaymentService payments) : IParticipantCommandHandler
{
    public async Task<ParticipantOutcome?> HandleAsync(ParticipantCommand command, string replyTo, CancellationToken cancellationToken)
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
            case ("RefundReturnPayment", false):
                var returned = command.Bind<RefundReturnPaymentRequest>();
                var refundError = await payments.RefundReturnAsync(
                    returned.ReturnId, returned.OrderId, returned.Amount, returned.FailStep, cancellationToken);
                return ParticipantOutcomeFactory.Create(command, returned.ReturnId, returned.OrderId, refundError);
            default:
                throw new JsonException("Unsupported payment command type or execution kind.");
        }

        return ParticipantOutcomeFactory.Create(command, orderId, error);
    }

    private sealed record AuthorizePaymentRequest(Guid Id, decimal Total, string? FailStep = null);

    private sealed record RefundPaymentRequest(Guid Id);

    private sealed record RefundReturnPaymentRequest(Guid ReturnId, Guid OrderId, decimal Amount, string? FailStep = null);
}
