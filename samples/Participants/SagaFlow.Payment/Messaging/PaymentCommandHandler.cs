using SagaFlow.Participants.Messaging;
using SagaFlow.Payment.Services;

namespace SagaFlow.Payment.Messaging;

public sealed class PaymentCommandHandler(IPaymentService payments) : IParticipantCommandHandler
{
    public IReadOnlyCollection<string> CommandNames { get; } = ["AuthorizePayment", "RefundPayment", "RefundReturnPayment"];

    public ParticipantCommandResult Handle(ParticipantCommand command, string? replyTo = null)
    {
        if (SampleFailureInjection.IsRequested(command, "payment", "AuthorizePayment"))
            return ParticipantCommandResult.Failure("payment");

        if (command.Name == "RefundReturnPayment")
        {
            if (command.Headers.IsCompensation) throw new InvalidOperationException("Return refund is a forward operation.");
            if (SampleFailureInjection.IsRequested(command, "payment", "RefundReturnPayment"))
                return ParticipantCommandResult.Failure("payment");
            var request = command.Bind<RefundReturnPaymentRequest>();
            return ParticipantCommandResult.Success(payments.RefundReturnPayment(
                request.ReturnId.ToString("N"), request.OrderId.ToString("N"), request.Amount));
        }
        return command.Name switch
        {
            "AuthorizePayment" => Authorize(command.Bind<AuthorizePaymentRequest>()),
            "RefundPayment" => ParticipantCommandResult.Success(payments.RefundPayment(command.Bind<RefundPaymentRequest>().Id.ToString("N"))),
            _ => throw new InvalidOperationException($"Unsupported payment command '{command.Name}'.")
        };
    }
    private ParticipantCommandResult Authorize(AuthorizePaymentRequest request) =>
        ParticipantCommandResult.Success(payments.AuthorizePayment(request.Id.ToString("N"), request.Total));
}
