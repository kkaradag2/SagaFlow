using System.Text.Json;
using SagaFlow.Participants.Messaging;
using Sample.Participants.Inventory.Domain;
using Sample.Participants.Inventory.Services;
using Sample.Participants.Messaging;

namespace Sample.Participants.Inventory.Messaging;

internal sealed class InventoryCommandHandler(InventoryService inventory) : IParticipantCommandHandler
{
    public async Task<ParticipantOutcome?> HandleAsync(ParticipantCommand command, string replyTo, CancellationToken cancellationToken)
    {
        switch (command.Name, command.Headers.IsCompensation)
        {
            case ("ReserveInventory", false):
                var reserve = command.Bind<ReserveInventoryRequest>();
                var reserveError = await inventory.ReserveAsync(reserve.Id, reserve.Items, reserve.FailStep, cancellationToken);
                return ParticipantOutcomeFactory.Create(command, reserve.Id, reserveError);
            case ("ReleaseInventory", true):
                var release = command.Bind<ReleaseInventoryRequest>();
                var releaseError = await inventory.ReleaseAsync(release.Id, cancellationToken);
                return ParticipantOutcomeFactory.Create(command, release.Id, releaseError);
            case ("RestockReturn", false):
                var returned = command.Bind<RestockReturnRequest>();
                var restockError = await inventory.RestockReturnAsync(
                    returned.ReturnId, returned.OrderId, returned.Items, returned.FailStep, cancellationToken);
                return ParticipantOutcomeFactory.Create(command, returned.ReturnId, returned.OrderId, restockError);
            default:
                throw new JsonException("Unsupported inventory command type or execution kind.");
        }
    }

    private sealed record ReserveInventoryRequest(Guid Id, InventoryLine[] Items, string? FailStep = null);

    private sealed record ReleaseInventoryRequest(Guid Id);

    private sealed record RestockReturnRequest(Guid ReturnId, Guid OrderId, InventoryLine[] Items, string? FailStep = null);
}
