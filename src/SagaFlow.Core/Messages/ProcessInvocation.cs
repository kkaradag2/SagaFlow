using System.Text.Json;
using System.Text.RegularExpressions;

namespace SagaFlow.Core.Messages;

internal static class InvocationGuard
{
    internal static string Text(string value) => !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException("Identity must not be empty.");
    internal static string Id(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? value : throw new ArgumentException("Invocation/message ID must be a nonempty UUID.");
    internal static string Name(string value) => Regex.IsMatch(value ?? "", @"\A[A-Za-z][A-Za-z0-9_-]{0,127}\z") ? value! : throw new ArgumentException("Invalid process identifier.");
    internal static string Version(string value) => Regex.IsMatch(value ?? "", @"\A(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:\.(?:0|[1-9][0-9]*))?\z") ? value! : throw new ArgumentException("Invalid version.");
}
public sealed record ProcessIdentity(string Domain, string Process, string Version)
{
    public string Domain { get; } = InvocationGuard.Name(Domain);
    public string Process { get; } = InvocationGuard.Name(Process);
    public string Version { get; } = InvocationGuard.Version(Version);
}
public sealed record ProcessInvocationIdentity(string InvocationId, string CorrelationId,
    ProcessIdentity Source, string SourceSagaInstanceId, string SourceStep, ProcessIdentity Target)
{
    public string InvocationId { get; } = InvocationGuard.Id(InvocationId);
    public string CorrelationId { get; } = InvocationGuard.Text(CorrelationId);
    public ProcessIdentity Source { get; } = Source ?? throw new ArgumentNullException(nameof(Source));
    public ProcessIdentity Target { get; } = Target ?? throw new ArgumentNullException(nameof(Target));
    public string SourceSagaInstanceId { get; } = InvocationGuard.Text(SourceSagaInstanceId);
    public string SourceStep { get; } = InvocationGuard.Name(SourceStep);
}
/// <summary>MessageId identifies one logical message; redelivery retains it. OccurredAt is producer UTC event time.</summary>
public sealed record InvocationMessage(ProcessInvocationIdentity Identity, string MessageId, DateTimeOffset OccurredAt)
{
    public void ValidateAgainst(ProcessInvocationIdentity expected)
    {
        if (Identity != expected) throw new InvalidOperationException("Invocation source, target or correlation mismatch.");
    }
    public ProcessInvocationIdentity Identity { get; } = Identity ?? throw new ArgumentNullException(nameof(Identity));
    public string MessageId { get; } = InvocationGuard.Id(MessageId);
    public DateTimeOffset OccurredAt { get; } = OccurredAt != default ? OccurredAt.ToUniversalTime() : throw new ArgumentException("Event time is required.");
}
public sealed record ProcessInvocationRequest(ProcessInvocationIdentity Identity, JsonElement Payload, InvocationMessage? Message = null)
{
    public ProcessInvocationIdentity Identity { get; } = Identity ?? throw new ArgumentNullException(nameof(Identity));
    public InvocationMessage Message { get; } = Message is null ? new(Identity, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow)
        : Message.Identity == Identity ? Message : throw new ArgumentException("Request message identity mismatch.");
    public JsonElement Payload { get; } = Payload.ValueKind != JsonValueKind.Undefined ? Payload.Clone() : throw new ArgumentException("Payload must be JSON.");

}
public sealed record ProcessInvocationAccepted(InvocationMessage Message, string TargetSagaInstanceId)
{
    public InvocationMessage Message { get; } = Message ?? throw new ArgumentNullException(nameof(Message));
    public string TargetSagaInstanceId { get; } = InvocationGuard.Text(TargetSagaInstanceId);
}
public sealed record ProcessInvocationRejected(InvocationMessage Message, string Reason)
{
    public InvocationMessage Message { get; } = Message ?? throw new ArgumentNullException(nameof(Message));
    public string Reason { get; } = InvocationGuard.Text(Reason);
}
public enum ExternalProcessStatus { Prepared, Requested, Accepted, Rejected, Completed, Failed, Compensated, Running }
public sealed record ProcessInvocationTerminalOutcome(InvocationMessage Message, string TargetSagaInstanceId, ExternalProcessStatus Status)
{
    public InvocationMessage Message { get; } = Message ?? throw new ArgumentNullException(nameof(Message));
    public string TargetSagaInstanceId { get; } = InvocationGuard.Text(TargetSagaInstanceId);
    public ExternalProcessStatus Status { get; } = Status is ExternalProcessStatus.Completed or ExternalProcessStatus.Failed or ExternalProcessStatus.Compensated ? Status : throw new ArgumentException("Terminal status required.");
}
/// <summary>Prepared means only local registration; no request has been sent.</summary>
public sealed record ProcessInvocationReceipt(string InvocationId, ProcessIdentity Target, ExternalProcessStatus Status)
{
    public string InvocationId { get; } = InvocationGuard.Id(InvocationId);
    public ProcessIdentity Target { get; } = Target ?? throw new ArgumentNullException(nameof(Target));
    public ExternalProcessStatus Status { get; } = Status is ExternalProcessStatus.Prepared or ExternalProcessStatus.Requested ? Status : throw new ArgumentException("Receipt must describe local registration/request only.");
}
public sealed record CrossDomainInstanceRelationship(ProcessInvocationIdentity Identity, string? TargetSagaInstanceId,
    ExternalProcessStatus LastKnownStatus, DateTimeOffset UpdatedAt, DateTimeOffset? CreatedAt = null, DateTimeOffset? LastObservedAt = null)
{
    public ProcessInvocationIdentity Identity { get; } = Identity ?? throw new ArgumentNullException(nameof(Identity));
    public string? TargetSagaInstanceId { get; } = LastKnownStatus is ExternalProcessStatus.Prepared or ExternalProcessStatus.Requested or ExternalProcessStatus.Rejected
        ? (TargetSagaInstanceId is null ? null : InvocationGuard.Text(TargetSagaInstanceId)) : InvocationGuard.Text(TargetSagaInstanceId!);
    public ExternalProcessStatus LastKnownStatus { get; } = Enum.IsDefined(LastKnownStatus) ? LastKnownStatus : throw new ArgumentException("Unknown external status.");
    public DateTimeOffset UpdatedAt { get; } = UpdatedAt != default ? UpdatedAt.ToUniversalTime() : throw new ArgumentException("Update time required.");
}
