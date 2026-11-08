using System.Text.Json;

namespace SagaFlow.Participants.Messaging;

/// <summary>Sample-local transport handle; contains no application business data or Saga algorithm.</summary>
public readonly record struct DeferredReplyHandle(Guid Value);
public sealed record DeferredPublication(ParticipantOutcome Outcome, string ReplyTo);

/// <summary>Process-local AMQP context and business-record association. Never serialized into business records.</summary>
public sealed class DeferredReplies
{
    private sealed record Entry(DeferredReplyHandle Handle, Guid BusinessRecordId, ParticipantCommand Command, string ReplyTo);
    private readonly object gate = new();
    private readonly Dictionary<(string Saga, string Step), Entry> executions = [];
    private readonly Dictionary<Guid, DeferredReplyHandle> business = [];
    private readonly Dictionary<DeferredReplyHandle, Entry> replies = [];

    // The synchronous callback registers only local business data; no publication occurs under this gate.
    public DeferredReplyHandle Register(ParticipantCommand command, string? replyTo, Func<Guid> registerBusiness)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replyTo);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Headers.SagaInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Headers.CorrelationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Headers.StepId);
        lock (gate)
        {
            var key = (command.Headers.SagaInstanceId, command.Headers.StepId);
            if (executions.TryGetValue(key, out var existing))
            {
                if (existing.ReplyTo != replyTo || existing.Command.Name != command.Name ||
                    !existing.Command.Headers.SameExecution(command.Headers) ||
                    !JsonElement.DeepEquals(existing.Command.Payload, command.Payload))
                    throw new InvalidOperationException("Deferred command has conflicting transport context or business input.");
                return existing.Handle;
            }
            var id = registerBusiness();
            var handle = new DeferredReplyHandle(Guid.NewGuid());
            var entry = new Entry(handle, id, command, replyTo);
            business.Add(id, handle);
            executions.Add(key, entry);
            replies.Add(handle, entry);
            return handle;
        }
    }

    public DeferredReplyHandle ForBusinessRecord(Guid id)
    {
        lock (gate) return business.TryGetValue(id, out var handle) ? handle
            : throw new KeyNotFoundException("Deferred transport context is unavailable; manual reconciliation is required.");
    }

    public DeferredPublication Prepare(DeferredReplyHandle handle, string name, object result, bool success = true)
    {
        lock (gate)
        {
            var entry = replies[handle];
            return new(new ParticipantOutcome { Name = name, Headers = entry.Command.Headers, IsSuccess = success,
                Result = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)) }, entry.ReplyTo);
        }
    }
}
