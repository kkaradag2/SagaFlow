using SagaFlow.Participants.Messaging;
using SagaFlow.Inventory.Services;
using SagaFlow.Inventory.Domain;

namespace SagaFlow.Inventory.Messaging;

public sealed class InventoryCommandHandler(IInventoryService inventory) : IParticipantCommandHandler
{
    public IReadOnlyCollection<string> CommandNames { get; } = ["ReserveInventory", "ReleaseInventory", "RestockReturn"];

    public ParticipantCommandResult Handle(ParticipantCommand command, string? replyTo = null)
    {
        if (SampleFailureInjection.IsRequested(command, "inventory", "ReserveInventory"))
            return ParticipantCommandResult.Failure("inventory");

        if (command.Name == "RestockReturn")
        {
            if (command.Headers.IsCompensation) throw new InvalidOperationException("Return restock is a forward operation.");
            if (SampleFailureInjection.IsRequested(command, "inventory", "RestockReturn"))
                return ParticipantCommandResult.Failure("inventory");
            var request = command.Bind<RestockReturnRequest>();
            return ParticipantCommandResult.Success(inventory.RestockReturn(
                request.ReturnId.ToString("N"), request.OrderId.ToString("N"), RequireItems(request.Items)));
        }
        return command.Name switch
        {
            "ReserveInventory" => Reserve(command.Bind<ReserveInventoryRequest>()),
            "ReleaseInventory" => ParticipantCommandResult.Success(inventory.ReleaseInventory(command.Bind<ReleaseInventoryRequest>().Id.ToString("N"))),
            _ => throw new InvalidOperationException($"Unsupported inventory command '{command.Name}'.")
        };
    }
    private ParticipantCommandResult Reserve(ReserveInventoryRequest request) =>
        ParticipantCommandResult.Success(inventory.ReserveInventory(request.Id.ToString("N"), RequireItems(request.Items)));
    private static InventoryLine[] RequireItems(InventoryLine[] items)
    {
        // System.Text.Json nullable enforcement does not cover collection elements.
        if (items.Any(item => item is null)) throw new ArgumentException("Inventory items must not contain null entries.", nameof(items));
        return items; // Existing services retain quantity/product/empty-list validation.
    }
}
