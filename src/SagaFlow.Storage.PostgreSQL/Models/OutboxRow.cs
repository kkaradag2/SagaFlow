using SagaFlow.Core.Messages;
using SagaFlow.Storage.PostgreSQL.Persistence;

namespace SagaFlow.Storage.PostgreSQL.Models;

internal sealed class OutboxRow
{
    public string MessageId { get; set; } = null!;
    public string SagaInstanceId { get; set; } = null!;
    public string Document { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset RetryAt { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public string? Owner { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int Attempts { get; set; }

    public OutboxMessage Read() => StorageJson.Read<OutboxMessage>(Document);
    public static OutboxRow Create(OutboxMessage message) => new()
    {
        MessageId = message.MessageId, SagaInstanceId = message.SagaInstanceId,
        Document = StorageJson.Write(message), CreatedAt = message.CreatedAt, RetryAt = message.CreatedAt,
        Owner = message.PreparationOwner, LeaseUntil = message.PreparationOwner is null ? null : DateTimeOffset.MaxValue
    };
}
