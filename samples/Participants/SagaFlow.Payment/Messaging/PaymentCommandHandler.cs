using SagaFlow.Core.Messages;
using SagaFlow.Participants.Messaging;
using SagaFlow.Payment.Services;

namespace SagaFlow.Payment.Messaging;

public sealed class PaymentCommandHandler(IPaymentService payments) : IParticipantCommandHandler
{
    public IReadOnlyCollection<string> CommandNames { get; } = ["AuthorizePayment", "RefundPayment", "RefundReturnPayment"];

    public ParticipantCommandResult Handle(SagaCommand command, string? replyTo = null)
    {
        if (SampleFailureInjection.IsRequested(command, "payment", "AuthorizePayment"))
            return ParticipantCommandResult.Failure("payment");

        if (command.Name == "RefundReturnPayment")
        {
            if (command.Headers.IsCompensation) throw new InvalidOperationException("Return refund is a forward operation.");
            if (SampleFailureInjection.IsRequested(command, "payment", "RefundReturnPayment"))
                return ParticipantCommandResult.Failure("payment");
            return ParticipantCommandResult.Success(payments.RefundReturnPayment(
                command.Payload.GetProperty("returnId").GetGuid().ToString("N"),
                command.Payload.GetProperty("orderId").GetGuid().ToString("N"),
                command.Payload.GetProperty("amount").GetDecimal()));
        }
        var orderId = command.Payload.GetProperty("id").GetGuid().ToString("N");
        return ParticipantCommandResult.Success(command.Name switch
        {
            "AuthorizePayment" => payments.AuthorizePayment(orderId, command.Payload.GetProperty("total").GetDecimal()),
            "RefundPayment" => payments.RefundPayment(orderId),
            _ => throw new InvalidOperationException($"Unsupported payment command '{command.Name}'.")
        });
    }
}
