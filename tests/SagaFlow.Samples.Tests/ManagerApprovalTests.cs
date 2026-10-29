using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SagaFlow.Core.Messages;
using SagaFlow.ManagerApproval;
using SagaFlow.ManagerApproval.Domain;
using SagaFlow.ManagerApproval.Messaging;
using SagaFlow.ManagerApproval.Repositories;

namespace SagaFlow.Samples.Tests;

public sealed class ManagerApprovalTests
{
    private sealed class RecordingPublisher : IApprovalOutcomePublisher
    {
        public ConcurrentQueue<(SagaOutcomeEvent Outcome, string ReplyTo)> Attempts { get; } = new();
        public ConcurrentQueue<(SagaOutcomeEvent Outcome, string ReplyTo)> Sent { get; } = new();
        public ConcurrentQueue<(SagaOutcomeEvent Outcome, string ReplyTo)> Confirmed { get; } = new();
        public Func<Task>? OnConnect { get; init; }
        public Func<Task>? OnCreateChannel { get; init; }
        public Func<Task>? OnPublish { get; init; }
        public async Task PublishAsync(SagaOutcomeEvent outcome, string replyTo, CancellationToken cancellationToken)
        {
            Assert.False(cancellationToken.CanBeCanceled);
            Attempts.Enqueue((outcome, replyTo));
            try
            {
                if (OnConnect is not null) await OnConnect();
                if (OnCreateChannel is not null) await OnCreateChannel();
            }
            catch (Exception error) { throw new ApprovalOutcomeNotSentException(error); }
            // Mirrors BasicPublishAsync entry: a failure after this point is delivery-uncertain.
            Sent.Enqueue((outcome, replyTo));
            if (OnPublish is not null) await OnPublish();
            Confirmed.Enqueue((outcome, replyTo));
        }
    }

    private static WebApplicationFactory<ManagerApprovalProgram> Factory(RecordingPublisher publisher) =>
        new WebApplicationFactory<ManagerApprovalProgram>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IApprovalOutcomePublisher>();
            services.AddSingleton<IApprovalOutcomePublisher>(publisher);
        }));

    private static SagaCommand Command(Guid orderId, string sagaId = "saga-1") => new()
    {
        Name = "RequestManagerApproval",
        Headers = new() { SagaInstanceId = sagaId, CorrelationId = $"correlation-{sagaId}", StepId = "managerApproval" },
        Payload = JsonSerializer.SerializeToElement(new { id = orderId, totalQuantity = 1001 })
    };

    private static ApprovalCommandHandler Handler(WebApplicationFactory<ManagerApprovalProgram> factory) =>
        new(factory.Services.GetRequiredService<InMemoryApprovalRepository>(), TimeProvider.System);

    [Fact]
    public async Task CommandAndRedeliveryCreateOnePendingApprovalWithoutPublishingAnOutcome()
    {
        var publisher = new RecordingPublisher();
        await using var factory = Factory(publisher);
        using var client = factory.CreateClient();
        var command = Command(Guid.NewGuid());
        var handler = Handler(factory);
        Assert.Null(handler.Handle(command, "order.outcomes"));
        Assert.Null(handler.Handle(command, "order.outcomes"));
        using var pending = JsonDocument.Parse(await client.GetStringAsync("/approvals/pending"));
        var record = Assert.Single(pending.RootElement.EnumerateArray());
        Assert.Equal("Pending", record.GetProperty("status").GetString());
        Assert.Equal(1001, record.GetProperty("totalQuantity").GetInt64());
        Assert.Equal(command.Headers.SagaInstanceId, record.GetProperty("sagaInstanceId").GetString());
        Assert.Equal(command.Headers.CorrelationId, record.GetProperty("correlationId").GetString());
        Assert.Equal("managerApproval", record.GetProperty("stepId").GetString());
        Assert.Equal("order.outcomes", record.GetProperty("replyTo").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/approvals/{command.Payload.GetProperty("id").GetGuid()}" )).StatusCode);
        Assert.Empty(publisher.Sent);
    }

    [Theory]
    [InlineData("approve", true, "Approved")]
    [InlineData("reject", false, "Rejected")]
    public async Task DecisionPublishesOneSuccessfulOutcomeWithOriginalRoutingAndDuplicateReturnsConflict(string decision, bool approved, string status)
    {
        var publisher = new RecordingPublisher();
        await using var factory = Factory(publisher);
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();
        var command = Command(id);
        Handler(factory).Handle(command, "original-domain.outcomes");
        var response = await client.PostAsJsonAsync($"/approvals/{id}/decision", new { decision });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var record = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(status, record.RootElement.GetProperty("status").GetString());
        Assert.Equal(decision, record.RootElement.GetProperty("decision").GetString());
        var published = Assert.Single(publisher.Sent);
        Assert.True(published.Outcome.IsSuccess);
        Assert.Equal("RequestManagerApprovalCompleted", published.Outcome.Name);
        Assert.Equal(approved, published.Outcome.Result.GetProperty("approved").GetBoolean());
        Assert.Equal(command.Headers.SagaInstanceId, published.Outcome.Headers.SagaInstanceId);
        Assert.Equal(command.Headers.CorrelationId, published.Outcome.Headers.CorrelationId);
        Assert.Equal(command.Headers.StepId, published.Outcome.Headers.StepId);
        Assert.Equal("original-domain.outcomes", published.ReplyTo);
        // A command redelivery after decision must not reset the terminal record.
        Handler(factory).Handle(command, "original-domain.outcomes");
        Assert.Empty(factory.Services.GetRequiredService<InMemoryApprovalRepository>().Pending());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/approvals/{id}/decision", new { decision = "reject" })).StatusCode);
        Assert.Single(publisher.Sent);
    }

    [Fact]
    public async Task UnknownOrderReturnsNotFound()
    {
        await using var factory = Factory(new());
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/approvals/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/approvals/{id}/decision", new { decision = "approve" })).StatusCode);
    }

    [Fact]
    public void ConflictingRedeliveryCannotOverwriteOriginalSagaMetadata()
    {
        var repository = new InMemoryApprovalRepository();
        var handler = new ApprovalCommandHandler(repository, TimeProvider.System);
        var id = Guid.NewGuid();
        handler.Handle(Command(id), "original.outcomes");
        Assert.Throws<InvalidOperationException>(() => handler.Handle(Command(id), "different.outcomes"));
        Assert.Equal("original.outcomes", Assert.Single(repository.Pending()).ReplyTo);
    }

    [Fact]
    public async Task SwaggerDocumentsApprovalEndpointsAndParticipantDoesNotRegisterSagaFlow()
    {
        await using var factory = Factory(new()).WithWebHostBuilder(builder => builder.UseSetting("environment", "Development"));
        using var client = factory.CreateClient();
        var swagger = await client.GetStringAsync("/swagger/v1/swagger.json");
        Assert.Contains("/approvals/pending", swagger);
        Assert.Contains("/approvals/{orderId}/decision", swagger);
        Assert.Null(factory.Services.GetService<SagaFlow.Core.Abstractions.ISagaFlow>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Approve")]
    [InlineData("other")]
    public async Task InvalidDecisionReturnsBadRequestAndLeavesPending(string? decision)
    {
        var publisher = new RecordingPublisher();
        await using var factory = Factory(publisher);
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();
        Handler(factory).Handle(Command(id), "order.outcomes");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/approvals/{id}/decision", new { decision })).StatusCode);
        Assert.Single(factory.Services.GetRequiredService<InMemoryApprovalRepository>().Pending());
        Assert.Empty(publisher.Sent);
    }

    [Fact]
    public async Task MultipleSagasForSameOrderAreNotSilentlyDisambiguated()
    {
        var publisher = new RecordingPublisher();
        await using var factory = Factory(publisher);
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();
        Handler(factory).Handle(Command(id, "saga-a"), "order.outcomes");
        Handler(factory).Handle(Command(id, "saga-b"), "order.outcomes");
        Assert.Equal(2, factory.Services.GetRequiredService<InMemoryApprovalRepository>().Pending().Count);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync($"/approvals/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/approvals/{id}/decision", new { decision = "approve" })).StatusCode);
        Assert.Empty(publisher.Sent);
    }

    [Fact]
    public async Task DifferentOrdersPublishOnlyToTheirOwnSagaAndReplyTo()
    {
        var publisher = new RecordingPublisher();
        await using var factory = Factory(publisher);
        using var client = factory.CreateClient();
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        Handler(factory).Handle(Command(first, "saga-a"), "a.outcomes");
        Handler(factory).Handle(Command(second, "saga-b"), "b.outcomes");
        await client.PostAsJsonAsync($"/approvals/{second}/decision", new { decision = "reject" });
        await client.PostAsJsonAsync($"/approvals/{first}/decision", new { decision = "approve" });
        Assert.Equal(new[] { "saga-b", "saga-a" }, publisher.Sent.Select(item => item.Outcome.Headers.SagaInstanceId));
        Assert.Equal(new[] { "b.outcomes", "a.outcomes" }, publisher.Sent.Select(item => item.ReplyTo));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureAfterPublishStartedRetainsUncertaintyReturns503AndCannotRepublish(bool cancellation)
    {
        var publisher = new RecordingPublisher { OnPublish = () => cancellation
            ? Task.FromCanceled(new CancellationToken(true)) : Task.FromException(new IOException("broker unavailable")) };
        await using var factory = Factory(publisher);
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();
        Handler(factory).Handle(Command(id), "order.outcomes");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync($"/approvals/{id}/decision", new { decision = "approve" })).StatusCode);
        var retained = Assert.Single(factory.Services.GetRequiredService<InMemoryApprovalRepository>().Find(id));
        Assert.Equal(ApprovalStatus.PublishFailed, retained.Status);
        Assert.Equal("approve", retained.Decision);
        Assert.False(string.IsNullOrWhiteSpace(retained.PublishError));
        Assert.Null(retained.PublishedAt);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/approvals/{id}/decision", new { decision = "approve" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/approvals/{id}/decision", new { decision = "reject" })).StatusCode);
        Assert.Single(publisher.Attempts);
        Assert.Single(publisher.Sent);
        Assert.Empty(publisher.Confirmed);
    }

    [Theory]
    [InlineData("connection", "approve", true)]
    [InlineData("connection", "reject", false)]
    [InlineData("channel", "approve", true)]
    [InlineData("channel", "reject", false)]
    public async Task FailureBeforePublishCanRetryOnlySameDecisionAndPublishesOneOutcomeWithOriginalMetadata(string failedStage, string decision, bool approved)
    {
        var preparationAttempts = 0;
        Task Prepare() => Interlocked.Increment(ref preparationAttempts) == 1
            ? Task.FromException(new IOException($"{failedStage} creation failed")) : Task.CompletedTask;
        var publisher = new RecordingPublisher
        {
            OnConnect = failedStage == "connection" ? Prepare : null,
            OnCreateChannel = failedStage == "channel" ? Prepare : null
        };
        await using var factory = Factory(publisher);
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();
        var command = Command(id);
        Handler(factory).Handle(command, "original-domain.outcomes");
        var repository = factory.Services.GetRequiredService<InMemoryApprovalRepository>();

        var failure = await client.PostAsJsonAsync($"/approvals/{id}/decision", new { decision });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
        using var failureBody = JsonDocument.Parse(await failure.Content.ReadAsStringAsync());
        Assert.Equal("PublishNotSent", failureBody.RootElement.GetProperty("status").GetString());
        var retained = Assert.Single(repository.Find(id));
        Assert.Equal(ApprovalStatus.PublishNotSent, retained.Status);
        Assert.Equal(decision, retained.Decision);
        Assert.NotNull(retained.DecidedAt);
        Assert.Contains($"{failedStage} creation failed", retained.PublishError);
        Assert.Single(publisher.Attempts);
        Assert.Empty(publisher.Sent);
        Assert.Empty(publisher.Confirmed);

        var otherDecision = approved ? "reject" : "approve";
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/approvals/{id}/decision", new { decision = otherDecision })).StatusCode);
        Assert.Equal(retained, Assert.Single(repository.Find(id)));
        Assert.Single(publisher.Attempts);
        Handler(factory).Handle(command, "original-domain.outcomes");
        Assert.Equal(retained, Assert.Single(repository.Find(id)));

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/approvals/{id}/decision", new { decision })).StatusCode);
        var completed = Assert.Single(repository.Find(id));
        Assert.Equal(approved ? ApprovalStatus.Approved : ApprovalStatus.Rejected, completed.Status);
        Assert.Equal(retained.DecidedAt, completed.DecidedAt);
        Assert.Equal(retained.RequestedAt, completed.RequestedAt);
        Assert.NotNull(completed.PublishedAt);
        Assert.Null(completed.PublishError);
        Assert.Equal(2, publisher.Attempts.Count);
        var sent = Assert.Single(publisher.Sent);
        Assert.Single(publisher.Confirmed);
        Assert.True(sent.Outcome.IsSuccess);
        Assert.Equal(approved, sent.Outcome.Result.GetProperty("approved").GetBoolean());
        Assert.Equal(command.Headers.SagaInstanceId, sent.Outcome.Headers.SagaInstanceId);
        Assert.Equal(command.Headers.CorrelationId, sent.Outcome.Headers.CorrelationId);
        Assert.Equal(command.Headers.StepId, sent.Outcome.Headers.StepId);
        Assert.Equal("original-domain.outcomes", sent.ReplyTo);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/approvals/{id}/decision", new { decision })).StatusCode);
        Assert.Equal(2, publisher.Attempts.Count);
        Assert.Single(publisher.Sent);
    }

    [Fact]
    public async Task ConcurrentManualRetryCannotStartAnotherPublishWhileChannelCreationIsWaiting()
    {
        var attempts = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new RecordingPublisher
        {
            OnConnect = () => Interlocked.Increment(ref attempts) == 1
                ? Task.FromException(new IOException("connection unavailable")) : Task.CompletedTask,
            OnCreateChannel = async () => { entered.SetResult(); await release.Task; }
        };
        await using var factory = Factory(publisher);
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();
        Handler(factory).Handle(Command(id), "order.outcomes");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync($"/approvals/{id}/decision", new { decision = "approve" })).StatusCode);
        var retry = client.PostAsJsonAsync($"/approvals/{id}/decision", new { decision = "approve" });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ApprovalStatus.Publishing, Assert.Single(factory.Services.GetRequiredService<InMemoryApprovalRepository>().Find(id)).Status);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/approvals/{id}/decision", new { decision = "approve" })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/approvals/{id}/decision", new { decision = "reject" })).StatusCode);
            Assert.Equal(2, publisher.Attempts.Count);
            Assert.Empty(publisher.Sent);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(HttpStatusCode.OK, (await retry).StatusCode);
        Assert.Single(publisher.Sent);
        Assert.Single(publisher.Confirmed);
    }

    [Fact]
    public async Task RetryDoesNotSelectAnotherSagaWhenOrderBecomesAmbiguous()
    {
        var publisher = new RecordingPublisher { OnConnect = () => Task.FromException(new IOException("connection unavailable")) };
        await using var factory = Factory(publisher);
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();
        Handler(factory).Handle(Command(id, "saga-a"), "a.outcomes");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync($"/approvals/{id}/decision", new { decision = "approve" })).StatusCode);
        Handler(factory).Handle(Command(id, "saga-b"), "b.outcomes");
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/approvals/{id}/decision", new { decision = "approve" })).StatusCode);
        var records = factory.Services.GetRequiredService<InMemoryApprovalRepository>().Find(id);
        Assert.Equal(ApprovalStatus.PublishNotSent, records.Single(record => record.SagaInstanceId == "saga-a").Status);
        Assert.Equal(ApprovalStatus.Pending, records.Single(record => record.SagaInstanceId == "saga-b").Status);
        Assert.Single(publisher.Attempts);
        Assert.Empty(publisher.Sent);
    }

    [Fact]
    public async Task SafeRetryThatReachesPublishThenFailsBecomesUncertainAndCannotRetryAgain()
    {
        var preparations = 0;
        var publisher = new RecordingPublisher
        {
            OnConnect = () => Interlocked.Increment(ref preparations) == 1
                ? Task.FromException(new IOException("connection unavailable")) : Task.CompletedTask,
            OnPublish = () => Task.FromException(new IOException("connection lost after publish started"))
        };
        await using var factory = Factory(publisher);
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();
        Handler(factory).Handle(Command(id), "order.outcomes");
        var repository = factory.Services.GetRequiredService<InMemoryApprovalRepository>();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync($"/approvals/{id}/decision", new { decision = "approve" })).StatusCode);
        Assert.Equal(ApprovalStatus.PublishNotSent, Assert.Single(repository.Find(id)).Status);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync($"/approvals/{id}/decision", new { decision = "approve" })).StatusCode);
        Assert.Equal(ApprovalStatus.PublishFailed, Assert.Single(repository.Find(id)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/approvals/{id}/decision", new { decision = "approve" })).StatusCode);
        Assert.Equal(2, publisher.Attempts.Count);
        Assert.Single(publisher.Sent);
        Assert.Empty(publisher.Confirmed);
    }

    [Fact]
    public async Task ConcurrentDecisionsCannotPublishAnotherOutcomeWhileFirstPublicationIsWaiting()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new RecordingPublisher { OnPublish = async () => { entered.SetResult(); await release.Task; } };
        await using var factory = Factory(publisher);
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();
        Handler(factory).Handle(Command(id), "order.outcomes");
        var first = client.PostAsJsonAsync($"/approvals/{id}/decision", new { decision = "approve" });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ApprovalStatus.Publishing, Assert.Single(factory.Services.GetRequiredService<InMemoryApprovalRepository>().Find(id)).Status);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/approvals/{id}/decision", new { decision = "reject" })).StatusCode);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
        Assert.Single(publisher.Sent);
    }
}
