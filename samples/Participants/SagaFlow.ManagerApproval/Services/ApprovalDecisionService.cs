using System.Text.Json;
using SagaFlow.Core.Messages;
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
        var outcome = new SagaOutcomeEvent
        {
            Name = "RequestManagerApprovalCompleted", IsSuccess = true,
            Headers = new() { SagaInstanceId = claim.SagaInstanceId, CorrelationId = claim.CorrelationId, StepId = claim.StepId },
            Result = JsonSerializer.SerializeToElement(new { approved })
        };
        try
        {
            // Once claimed, an HTTP disconnect must not strand Publishing. The RabbitMQ publisher has its own bounded deadline.
            await publisher.PublishAsync(outcome, claim.ReplyTo, CancellationToken.None);
        }
        catch (ApprovalOutcomeNotSentException error)
        {
            logger.LogError(error, "Approval outcome was not sent for Saga {SagaInstanceId}; the same decision may be retried manually.", claim.SagaInstanceId);
            return new(false, approvals.CompletePublication(claim, error.Message, timeProvider.GetUtcNow(), definitelyNotSent: true));
        }
        catch (Exception error)
        {
            logger.LogError(error, "Approval outcome publication failed for Saga {SagaInstanceId}.", claim.SagaInstanceId);
            // Delivery may be uncertain. Keep the decision and block resubmission rather than silently issuing another outcome.
            return new(false, approvals.CompletePublication(claim, error.Message, timeProvider.GetUtcNow()));
        }
        return new(true, approvals.CompletePublication(claim, null, timeProvider.GetUtcNow()));
    }
}
