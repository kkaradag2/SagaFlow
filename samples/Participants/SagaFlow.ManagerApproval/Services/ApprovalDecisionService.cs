using SagaFlow.ManagerApproval.Domain;
using SagaFlow.ManagerApproval.Messaging;
using SagaFlow.ManagerApproval.Repositories;

namespace SagaFlow.ManagerApproval.Services;

public sealed record ApprovalDecisionResult(bool Published, ApprovalRecord Approval);

public sealed class ApprovalDecisionService(InMemoryApprovalRepository approvals, IApprovalOutcomePublisher publisher,
    TimeProvider timeProvider, ILogger<ApprovalDecisionService> logger)
{
    public async Task<ApprovalDecisionResult> DecideAsync(Guid orderId, bool approved, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var claim = approvals.BeginDecision(orderId, approved, timeProvider.GetUtcNow());
        try
        {
            // Once claimed, an HTTP disconnect must not strand Publishing. The RabbitMQ publisher has its own bounded deadline.
            await publisher.PublishAsync(claim.ApprovalId, approved, CancellationToken.None);
        }
        catch (ApprovalOutcomeNotSentException error)
        {
            logger.LogError(error, "Approval outcome was not sent for approval {ApprovalId}; the same decision may be retried manually.", claim.ApprovalId);
            return new(false, approvals.CompletePublication(claim, error.Message, timeProvider.GetUtcNow(), definitelyNotSent: true));
        }
        catch (Exception error)
        {
            logger.LogError(error, "Approval outcome publication failed for approval {ApprovalId}.", claim.ApprovalId);
            // Delivery may be uncertain. Keep the decision and block resubmission rather than silently issuing another outcome.
            return new(false, approvals.CompletePublication(claim, error.Message, timeProvider.GetUtcNow()));
        }
        return new(true, approvals.CompletePublication(claim, null, timeProvider.GetUtcNow()));
    }
}
