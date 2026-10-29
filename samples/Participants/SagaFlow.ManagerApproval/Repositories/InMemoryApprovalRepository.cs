using SagaFlow.ManagerApproval.Domain;

namespace SagaFlow.ManagerApproval.Repositories;

public sealed class InMemoryApprovalRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Saga, string Step), ApprovalRecord> _approvals = [];

    public ApprovalRecord Register(ApprovalRecord request)
    {
        lock (_gate)
        {
            var key = (request.SagaInstanceId, request.StepId);
            if (_approvals.TryGetValue(key, out var current))
            {
                if (current.OrderId != request.OrderId || current.TotalQuantity != request.TotalQuantity
                    || current.CorrelationId != request.CorrelationId || current.ReplyTo != request.ReplyTo)
                    throw new InvalidOperationException("Duplicate approval command has conflicting metadata.");
                return current; // Redelivery must never reset a decision or create another pending approval.
            }
            _approvals.Add(key, request);
            return request;
        }
    }

    public IReadOnlyList<ApprovalRecord> Pending()
    {
        lock (_gate) return _approvals.Values.Where(item => item.Status == ApprovalStatus.Pending).OrderBy(item => item.RequestedAt).ToArray();
    }

    public IReadOnlyList<ApprovalRecord> Find(Guid orderId)
    {
        lock (_gate) return _approvals.Values.Where(item => item.OrderId == orderId).ToArray();
    }

    public ApprovalRecord BeginDecision(Guid orderId, bool approved, DateTimeOffset now)
    {
        lock (_gate)
        {
            var matches = _approvals.Values.Where(item => item.OrderId == orderId).ToArray();
            if (matches.Length == 0) throw new KeyNotFoundException("Approval was not found.");
            if (matches.Length != 1) throw new InvalidOperationException("Multiple Saga approvals exist for this Order ID; the request is ambiguous.");
            var current = matches[0];
            var decision = approved ? "approve" : "reject";
            if (current.Status == ApprovalStatus.PublishNotSent && current.Decision != decision)
                throw new InvalidOperationException("The recorded decision cannot be changed when retrying publication.");
            if (current.Status is not (ApprovalStatus.Pending or ApprovalStatus.PublishNotSent))
                throw new InvalidOperationException("Approval already has a decision or publication in progress; it cannot be submitted again.");
            var claimed = current with
            {
                Status = ApprovalStatus.Publishing, Decision = decision,
                DecidedAt = current.DecidedAt ?? now, PublishError = null
            };
            _approvals[(current.SagaInstanceId, current.StepId)] = claimed;
            return claimed;
        }
    }

    public ApprovalRecord CompletePublication(ApprovalRecord claim, string? error, DateTimeOffset now, bool definitelyNotSent = false)
    {
        lock (_gate)
        {
            var key = (claim.SagaInstanceId, claim.StepId);
            var current = _approvals[key];
            if (current.Status != ApprovalStatus.Publishing || current.Decision != claim.Decision)
                throw new InvalidOperationException("Approval publication is not active.");
            var completed = current with
            {
                Status = error is not null ? (definitelyNotSent ? ApprovalStatus.PublishNotSent : ApprovalStatus.PublishFailed)
                    : current.Decision == "approve" ? ApprovalStatus.Approved : ApprovalStatus.Rejected,
                PublishedAt = error is null ? now : null, PublishError = error
            };
            _approvals[key] = completed;
            return completed;
        }
    }
}
