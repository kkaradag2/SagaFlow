using SagaFlow.Core.Messages;
using SagaFlow.Inventory.Domain;
using SagaFlow.Inventory.Services;
using SagaFlow.Participants.Messaging;

namespace SagaFlow.Inventory.Messaging;

public sealed class InventoryCommandHandler(IInventoryService inventory) : IParticipantCommandHandler
{
    public IReadOnlyCollection<string> CommandNames { get; } = ["ReserveInventory", "ReleaseInventory", "RestockReturn"];

    public ParticipantCommandResult Handle(SagaCommand command, string? replyTo = null)
    {
        if (SampleFailureInjection.IsRequested(command, "inventory", "ReserveInventory"))
            return ParticipantCommandResult.Failure("inventory");

        if (command.Name == "RestockReturn")
        {
            if (command.Headers.IsCompensation) throw new InvalidOperationException("Return restock is a forward operation.");
            if (SampleFailureInjection.IsRequested(command, "inventory", "RestockReturn"))
                return ParticipantCommandResult.Failure("inventory");
            return ParticipantCommandResult.Success(inventory.RestockReturn(
                command.Payload.GetProperty("returnId").GetGuid().ToString("N"),
                command.Payload.GetProperty("orderId").GetGuid().ToString("N"),
                command.Payload.GetProperty("items").EnumerateArray()
                    .Select(item => new InventoryLine(item.GetProperty("productId").GetString()!, item.GetProperty("quantity").GetInt32()))
                    .ToArray()));
        }
        var orderId = command.Payload.GetProperty("id").GetGuid().ToString("N");
        return ParticipantCommandResult.Success(command.Name switch
        {
            "ReserveInventory" => inventory.ReserveInventory(orderId, command.Payload.GetProperty("items")
                .EnumerateArray()
                .Select(item => new InventoryLine(
                    item.GetProperty("productId").GetString()!,
                    item.GetProperty("quantity").GetInt32()))
                .ToArray()),
            "ReleaseInventory" => inventory.ReleaseInventory(orderId),
            _ => throw new InvalidOperationException($"Unsupported inventory command '{command.Name}'.")
        });
    }
}
