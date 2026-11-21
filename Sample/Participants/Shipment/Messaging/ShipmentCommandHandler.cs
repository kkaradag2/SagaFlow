using System.Text.Json;
using SagaFlow.Participants.Messaging;
using Sample.Participants.Messaging;
using Sample.Participants.Shipment.Services;

namespace Sample.Participants.Shipment.Messaging;

internal sealed class ShipmentCommandHandler(ShipmentService shipments) : IParticipantCommandHandler
{
    public async Task<ParticipantOutcome?> HandleAsync(
        ParticipantCommand command, string replyTo, CancellationToken cancellationToken)
    {
        switch (command.Name, command.Headers.IsCompensation)
        {
            case ("CreateShipment", false):
                var create = command.Bind<CreateShipmentRequest>();
                var createError = await shipments.CreateAsync(create.Id, create.FailStep, cancellationToken);
                return ParticipantOutcomeFactory.Create(command, create.Id, createError);
            case ("CancelShipment", true):
                var cancel = command.Bind<CancelShipmentRequest>();
                var cancelError = await shipments.CancelAsync(cancel.Id, cancellationToken);
                return ParticipantOutcomeFactory.Create(command, cancel.Id, cancelError);
            default:
                throw new JsonException("Unsupported shipment command type or execution kind.");
        }
    }

    private sealed record CreateShipmentRequest(Guid Id, string? FailStep = null);

    private sealed record CancelShipmentRequest(Guid Id);
}
