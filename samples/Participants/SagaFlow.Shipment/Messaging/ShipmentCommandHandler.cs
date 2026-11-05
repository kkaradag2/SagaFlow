using SagaFlow.Participants.Messaging;
using SagaFlow.Shipment.Services;

namespace SagaFlow.Shipment.Messaging;

public sealed class ShipmentCommandHandler(IShipmentService shipments) : IParticipantCommandHandler
{
    public IReadOnlyCollection<string> CommandNames { get; } = ["CreateShipment", "CancelShipment"];

    public ParticipantCommandResult Handle(ParticipantCommand command, string? replyTo = null)
    {
        if (SampleFailureInjection.IsRequested(command, "shipment", "CreateShipment"))
            return ParticipantCommandResult.Failure("shipment");

        return command.Name switch
        {
            "CreateShipment" => Create(command.Bind<CreateShipmentRequest>()),
            "CancelShipment" => ParticipantCommandResult.Success(shipments.CancelShipment(command.Bind<CancelShipmentRequest>().Id.ToString("N"))),
            _ => throw new InvalidOperationException($"Unsupported shipment command '{command.Name}'.")
        };
    }
    private ParticipantCommandResult Create(CreateShipmentRequest request) =>
        ParticipantCommandResult.Success(shipments.CreateShipment(request.Id.ToString("N"), request.CustomerId));
}
