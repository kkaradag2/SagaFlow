using SagaFlow.Core.Messages;
using SagaFlow.Participants.Messaging;
using SagaFlow.Shipment.Services;

namespace SagaFlow.Shipment.Messaging;

public sealed class ShipmentCommandHandler(IShipmentService shipments) : IParticipantCommandHandler
{
    public IReadOnlyCollection<string> CommandNames { get; } = ["CreateShipment", "CancelShipment"];

    public ParticipantCommandResult Handle(SagaCommand command, string? replyTo = null)
    {
        if (SampleFailureInjection.IsRequested(command, "shipment", "CreateShipment"))
            return ParticipantCommandResult.Failure("shipment");

        var orderId = command.Payload.GetProperty("id").GetGuid().ToString("N");
        return ParticipantCommandResult.Success(command.Name switch
        {
            "CreateShipment" => shipments.CreateShipment(
                orderId,
                command.Payload.GetProperty("customerId").GetString()!),
            "CancelShipment" => shipments.CancelShipment(orderId),
            _ => throw new InvalidOperationException($"Unsupported shipment command '{command.Name}'.")
        });
    }
}
