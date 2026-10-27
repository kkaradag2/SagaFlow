using SagaFlow.Core.Messages;
using SagaFlow.Participants.Messaging;
using SagaFlow.Payment.Services;

namespace SagaFlow.Payment.Messaging;

public sealed class PaymentCommandHandler(IPaymentService payments) : IParticipantCommandHandler
{
    public IReadOnlyCollection<string> CommandNames { get; } = ["AuthorizePayment", "RefundPayment"];

    public object Handle(SagaCommand command)
    {
        var orderId = command.Payload.GetProperty("id").GetGuid().ToString("N");
        return command.Name switch
        {
            "AuthorizePayment" => payments.AuthorizePayment(orderId, command.Payload.GetProperty("total").GetDecimal()),
            "RefundPayment" => payments.RefundPayment(orderId),
            _ => throw new InvalidOperationException($"Unsupported payment command '{command.Name}'.")
        };
    }
}
