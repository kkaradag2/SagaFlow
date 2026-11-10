using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Definitions;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Engine.Coordination;
using SagaFlow.Engine.Messaging;
namespace SagaFlow.Engine.Tests;

public sealed class ProcessMessagingTests
{
    private sealed class Bus : IProcessInvocationBus
    {
        public ValueTask PublishOutboxAsync(OutboxMessage message, CancellationToken ct) => message.ToInvocation() switch
        {
            ProcessInvocationRequest r => PublishAsync(r, ct),
            ProcessInvocationAccepted a => PublishAsync(a, ct),
            ProcessInvocationRejected r => PublishAsync(r, ct),
            ProcessInvocationTerminalOutcome t => PublishAsync(t, ct),
            _ => throw new InvalidOperationException("Unknown invocation message.")
        };

        public List<ProcessInvocationRequest> Requests { get; } = [];
        public List<ProcessInvocationAccepted> Accepted { get; } = [];
        public List<ProcessInvocationRejected> Rejected { get; } = [];
        public Func<ProcessInvocationRequest, Task>? Request { get; set; }
        public Func<ProcessInvocationAccepted, Task>? Accept { get; set; }
        public Func<ProcessInvocationRejected, Task>? Reject { get; set; }
        public Func<ProcessInvocationTerminalOutcome, Task>? Terminal { get; set; }
        public async ValueTask PublishAsync(ProcessInvocationRequest request, CancellationToken ct = default) { lock (Requests) Requests.Add(request); if (Request is not null) await Request(request); }
        public async ValueTask PublishAsync(ProcessInvocationAccepted accepted, CancellationToken ct = default) { lock (Accepted) Accepted.Add(accepted); if (Accept is not null) await Accept(accepted); }
        public async ValueTask PublishAsync(ProcessInvocationRejected rejected, CancellationToken ct = default) { lock (Rejected) Rejected.Add(rejected); if (Reject is not null) await Reject(rejected); }
        public async ValueTask PublishAsync(ProcessInvocationTerminalOutcome outcome, CancellationToken ct = default) { if (Terminal is not null) await Terminal(outcome); }
    }
    private sealed class FinalizationFailureStore(ISagaStateStore inner) : ISagaStateStore
    {
        private int marks;
        public ValueTask SaveAsync(SagaInstance instance, CancellationToken ct = default) => inner.SaveAsync(instance, ct);
        public ValueTask<SagaInstance?> FindAsync(string id, CancellationToken ct = default) => inner.FindAsync(id, ct);
        public ValueTask<IReadOnlyList<SagaInstance>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);
        public ValueTask<T> UpdateAsync<T>(string id, Func<SagaInstance, T> update, CancellationToken ct = default) => inner.UpdateAsync(id, update, ct);
        public ValueTask<T> UpdateWithOutboxAsync<T>(string id, Func<SagaInstance, (T Result, OutboxMessage? Message)> update, CancellationToken ct = default) => inner.UpdateWithOutboxAsync(id, update, ct);
        public ValueTask<IReadOnlyList<OutboxClaim>> ClaimOutboxAsync(string owner, DateTimeOffset now, TimeSpan lease, int limit, CancellationToken ct = default) => inner.ClaimOutboxAsync(owner, now, lease, limit, ct);
        public ValueTask MarkOutboxPublishedAsync(string id, string owner, DateTimeOffset at, CancellationToken ct = default) =>
            Interlocked.Increment(ref marks) == 1
                ? ValueTask.FromException(new IOException("Publish bookkeeping commit failed."))
                : inner.MarkOutboxPublishedAsync(id, owner, at, ct);
        public ValueTask ReleaseOutboxAsync(string id, string owner, DateTimeOffset at, CancellationToken ct = default) => inner.ReleaseOutboxAsync(id, owner, at, ct);
    }
    private sealed class Boundary(Func<SagaCommand, CancellationToken, Task> execute) : ILocalSagaExecutionBoundary
    {
        public Task ExecuteAsync(SagaCommand command, CancellationToken ct) => execute(command, ct);
    }
    private sealed class Policy : IProcessInvocationPolicy
    {
        public int Calls { get; private set; }
        public string? Reason { get; set; }
        public ValueTask<string?> ValidateAsync(ProcessInvocationRequest request, CancellationToken ct) { Calls++; return ValueTask.FromResult(Reason); }
    }
    private sealed class Setup
    {
        public CoordinationTestFixture Source { get; }
        public CoordinationTestFixture Target { get; }
        public Bus SourceBus { get; } = new();
        public Bus TargetBus { get; } = new();
        public Policy Policy { get; } = new();
        public ProcessInvocationManager SourceManager { get; }
        public ProcessInvocationPublisher Publisher { get; }
        public ProcessInvocationPublisher TargetPublisher { get; }
        public ProcessInvocationReceiver SourceReceiver { get; }
        public ProcessInvocationReceiver TargetReceiver { get; }
        public string Id { get; private set; } = "";
        public ProcessInvocationRequest Request { get; private set; } = null!;
        public Setup()
        {
            var target = new SagaDefinition { Domain = "target", Name = "remote", Version = "1.0",
                Steps = [new() { Name = "work", Command = "Work", Transitions = [new() { Event = "success", NextStep = "completed" }] }] };
            var source = new SagaDefinition { Domain = "source", Name = "local", Version = "1.0", Steps = target.Steps,
                BigPicture = [new() { Domain = "target", Process = "remote", Version = "1.0", Endpoint = "https://discovery-only", Relationship = "invokes" }] };
            Source = new(source, processBus: SourceBus); Target = new(target, processBus: TargetBus);
            SourceManager = new(Source.Store, Source.Registry, Source.Clock);
            var targetManager = new ProcessInvocationManager(Target.Store, Target.Registry, Target.Clock);
            Publisher = new(Source.Store, SourceBus, Source.Registry, Source.Clock, Source.Dispatcher);
            TargetPublisher = new(Target.Store, TargetBus, Target.Registry, Target.Clock, Target.Dispatcher);
            SourceReceiver = new(Source.Store, Source.Registry, SourceManager, SourceBus, Source.Clock, [], Source.Coordinator, Source.Dispatcher);
            TargetReceiver = new(Target.Store, Target.Registry, targetManager, TargetBus, Target.Clock, [Policy], Target.Coordinator, Target.Dispatcher);
            SourceBus.Request = request => TargetReceiver.ReceiveAsync(request);
            TargetBus.Accept = message => SourceReceiver.ReceiveAsync(message);
            TargetBus.Reject = message => SourceReceiver.ReceiveAsync(message);
            TargetBus.Terminal = message => SourceReceiver.ReceiveAsync(message);
        }
        public async Task Prepare()
        {
            Id = (await Source.Coordinator.StartAsync(new { Amount = 42 })).ToString("N");
            await SourceManager.PrepareAsync(Id, "work", "remote", new { Amount = 42 }, default);
            Request = (await Source.Store.FindAsync(Id))!.Steps[0].Invocation!;
        }
        public async Task<SagaInstance> State() => (await Source.Store.FindAsync(Id))!;
    }

    [Fact]
    public async Task RequestAcceptanceAndExplicitTerminalPropagationKeepSagaOwnershipSeparate()
    {
        var s = new Setup(); await s.Prepare();
        await s.Publisher.PublishAsync(s.Id, s.Request.Identity.InvocationId);
        var target = Assert.Single(await s.Target.Store.ListAsync());
        Assert.Equal(SagaStepStatus.Dispatched, target.Steps[0].Status);
        Assert.Single(s.Target.Bus.Commands);
        Assert.NotEqual(s.Request.Identity.CorrelationId, target.CorrelationId);
        Assert.Equal(s.Request.Identity, target.IncomingInvocation!.Identity);
        Assert.Equal(ExternalProcessStatus.Accepted, (await s.State()).Steps[0].InvocationProgress!.Status);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => s.TargetPublisher.TransitionTerminalAsync(target.Id, SagaStatus.Running));
        await s.TargetPublisher.TransitionTerminalAsync(target.Id, SagaStatus.Completed);
        await s.TargetPublisher.TransitionTerminalAsync(target.Id, SagaStatus.Completed);
        await s.SourceReceiver.ReceiveAsync(Assert.Single(s.TargetBus.Accepted)); // late duplicate acceptance must not regress terminal
        Assert.Equal(ExternalProcessStatus.Completed, (await s.State()).Steps[0].InvocationProgress!.Status);
        Assert.Equal(SagaStatus.Running, (await s.State()).Status);
        Assert.Equal("work", (await s.State()).CurrentStep);
    }

    [Fact]
    public async Task RejectedDecisionIsStickyAndDoesNotCreateTarget()
    {
        var s = new Setup(); await s.Prepare(); s.Policy.Reason = "Business rule rejected";
        await s.Publisher.PublishAsync(s.Id, s.Request.Identity.InvocationId);
        s.Policy.Reason = null;
        await s.TargetReceiver.ReceiveAsync(s.Request);
        Assert.Empty(await s.Target.Store.ListAsync());
        Assert.Equal(1, s.Policy.Calls);
        var rejection = Assert.Single(s.TargetBus.Rejected);
        await s.SourceReceiver.ReceiveAsync(rejection);
        Assert.Equal(rejection.Message.MessageId, (await s.Target.Store.FindIncomingDecisionAsync(s.Request.Identity.InvocationId))!.Reply.MessageId);
        Assert.Equal(ExternalProcessStatus.Rejected, (await s.State()).Steps[0].InvocationProgress!.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.SourceReceiver.ReceiveAsync(new ProcessInvocationAccepted(s.TargetBus.Rejected[0].Message, "invalid-target")));
    }

    [Theory]
    [InlineData(SagaStatus.Completed, ExternalProcessStatus.Completed)]
    [InlineData(SagaStatus.Failed, ExternalProcessStatus.Failed)]
    public async Task ConcurrentExplicitTerminalTransitionsApplyApplicationMutationAndPublishOnce(
        SagaStatus status, ExternalProcessStatus expected)
    {
        var s = new Setup();
        await s.Prepare();
        await s.Publisher.PublishAsync(s.Id, s.Request.Identity.InvocationId);
        var target = Assert.Single(await s.Target.Store.ListAsync());
        var mutations = 0;
        var publications = 0;
        s.TargetBus.Terminal = async response =>
        {
            var committed = (await s.Target.Store.FindAsync(target.Id))!;
            Assert.Equal(status, committed.Status);
            Assert.Equal(response, committed.InvocationTerminalOutcome);
            Assert.Equal("application-result", committed.Context.GetString());
            Interlocked.Increment(ref publications);
            await s.SourceReceiver.ReceiveAsync(response);
        };

        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
            s.TargetPublisher.TransitionTerminalAsync(target.Id, status, state =>
            {
                Interlocked.Increment(ref mutations);
                state.Context = System.Text.Json.JsonSerializer.SerializeToElement("application-result");
            }))));

        var terminal = (await s.Target.Store.FindAsync(target.Id))!;
        Assert.Equal(target.Revision + 12, terminal.Revision);
        Assert.Equal(1, mutations);
        Assert.Equal(1, publications);
        Assert.Null(terminal.CurrentStep);
        Assert.Equal(expected, terminal.InvocationTerminalOutcome!.Status);
        Assert.Equal(expected, (await s.State()).Steps[0].InvocationProgress!.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.TargetPublisher.TransitionTerminalAsync(target.Id, status == SagaStatus.Completed ? SagaStatus.Failed : SagaStatus.Completed));
        Assert.Equal(terminal.InvocationTerminalOutcome, (await s.Target.Store.FindAsync(target.Id))!.InvocationTerminalOutcome);
    }

    [Fact]
    public async Task InMemoryRejectsDirectIncomingTerminalWritesAndRollsBackAnOutboxCollision()
    {
        var s = new Setup();
        await s.Prepare();
        await s.Publisher.PublishAsync(s.Id, s.Request.Identity.InvocationId);
        var store = s.Target.Store;
        var target = Assert.Single(await store.ListAsync());
        await store.UpdateAsync(target.Id, state => { state.FailureReason = "still running"; return true; });
        var before = (await store.FindAsync(target.Id))!;
        var response = new ProcessInvocationTerminalOutcome(s.Request.Message, target.Id, ExternalProcessStatus.Failed);
        bool Mutate(SagaInstance state)
        {
            state.Status = SagaStatus.Failed;
            state.InvocationTerminalOutcome = response;
            state.InvocationTerminalQueued = true;
            return true;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.UpdateAsync(target.Id, Mutate));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.UpdateWithOutboxAsync(target.Id,
            state => (Mutate(state), (OutboxMessage?)null)));
        Mutate(before);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.SaveAsync(before));

        var seed = new SagaInstance { Id = s.Request.Identity.SourceSagaInstanceId };
        await store.SaveAsync(seed);
        await store.UpdateWithOutboxAsync(seed.Id, _ => (true, OutboxMessage.FromInvocation(s.Request)));
        await Assert.ThrowsAsync<IOException>(async () => await store.UpdateWithOutboxAsync(target.Id,
            state => (Mutate(state), OutboxMessage.FromInvocation(response))));
        var unchanged = (await store.FindAsync(target.Id))!;
        Assert.Equal(before.Revision, unchanged.Revision);
        Assert.Equal(SagaStatus.Running, unchanged.Status);
        Assert.Equal("still running", unchanged.FailureReason);
        Assert.Null(unchanged.InvocationTerminalOutcome);
        Assert.False(unchanged.InvocationTerminalQueued);
    }

    [Fact]
    public async Task FailedOrUncertainPublicationRemainsPendingForStableRetry()
    {
        var s = new Setup(); await s.Prepare();
        s.SourceBus.Request = _ => throw new IOException("confirmation lost");
        await s.Publisher.PublishAsync(s.Id, s.Request.Identity.InvocationId);
        var progress = (await s.State()).Steps[0];
        Assert.Equal(ExternalProcessStatus.Requested, progress.InvocationProgress!.Status);
        Assert.True(progress.Publication!.Queued);
        await MakeEligible(s.Source.Store, s.Request.Message.MessageId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Publisher.PublishAsync(s.Id, s.Request.Identity.InvocationId));
        s.SourceBus.Request = request => s.TargetReceiver.ReceiveAsync(request);
        await s.Publisher.PublishAsync(s.Id, s.Request.Identity.InvocationId, retry: true);
        Assert.Equal(s.SourceBus.Requests[0].Message, s.SourceBus.Requests[1].Message);
        Assert.Single(await s.Target.Store.ListAsync());
    }

    [Fact]
    public async Task PostPublishStoreFailureLeavesTheOriginalMessageRetryable()
    {
        var s = new Setup(); await s.Prepare(); s.SourceBus.Request = _ => Task.CompletedTask;
        var publisher = new ProcessInvocationPublisher(new FinalizationFailureStore(s.Source.Store), s.SourceBus, s.Source.Registry, s.Source.Clock, s.Source.Dispatcher);
        await publisher.PublishAsync(s.Id, s.Request.Identity.InvocationId);
        var state = await s.State();
        Assert.Equal(SagaStatus.Running, state.Status);
        Assert.Equal(ExternalProcessStatus.Requested, state.Steps[0].InvocationProgress!.Status);
        Assert.True(state.Steps[0].Publication!.Queued);
        Assert.Single(s.SourceBus.Requests);
        await MakeEligible(s.Source.Store, s.Request.Message.MessageId);
        await publisher.PublishAsync(s.Id, s.Request.Identity.InvocationId, retry: true);
        Assert.Equal(2, s.SourceBus.Requests.Count);
        Assert.Equal(s.SourceBus.Requests[0].Message, s.SourceBus.Requests[1].Message);
    }

    [Fact]
    public async Task LostAcceptanceCanBeReplayedWithoutRecreatingTarget()
    {
        var s = new Setup(); await s.Prepare(); s.TargetBus.Accept = _ => throw new IOException("acceptance not delivered");
        await s.Publisher.PublishAsync(s.Id, s.Request.Identity.InvocationId);
        var targetId = Assert.Single(await s.Target.Store.ListAsync()).Id;
        Assert.Equal(ExternalProcessStatus.Requested, (await s.State()).Steps[0].InvocationProgress!.Status);
        s.TargetBus.Accept = message => s.SourceReceiver.ReceiveAsync(message);
        await MakeEligible(s.Target.Store, Assert.Single(s.TargetBus.Accepted).Message.MessageId);
        await s.TargetReceiver.ReceiveAsync(s.Request);
        Assert.Equal(targetId, Assert.Single(await s.Target.Store.ListAsync()).Id);
        Assert.Equal(s.TargetBus.Accepted[0], s.TargetBus.Accepted[1]); Assert.Equal(1, s.Policy.Calls);
    }

    [Fact]
    public async Task ReservationSerializesAttemptsAndDefinesInFlightFailureBoundary()
    {
        var s = new Setup(); await s.Prepare();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.SourceBus.Request = async _ => { entered.SetResult(); await release.Task; };
        var first = s.Publisher.PublishAsync(s.Id, s.Request.Identity.InvocationId);
        await entered.Task;
        await s.Publisher.PublishAsync(s.Id, s.Request.Identity.InvocationId, retry: true);
        Assert.Single(s.SourceBus.Requests);
        await s.Source.Store.UpdateAsync(s.Id, instance => { instance.Status = SagaStatus.Failed; instance.Steps[0].Status = SagaStepStatus.Failed; return true; });
        release.SetResult(); await first;
        Assert.Single(s.SourceBus.Requests);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Publisher.PublishAsync(s.Id, s.Request.Identity.InvocationId, retry: true));
    }

    private static async Task MakeEligible(ISagaStateStore store, string messageId)
    {
        var claim = Assert.Single(await store.ClaimOutboxAsync("retry-fixture", DateTimeOffset.UtcNow.AddMinutes(1),
            TimeSpan.FromMinutes(1), 1));
        Assert.Equal(messageId, claim.Message.MessageId);
        await store.ReleaseOutboxAsync(messageId, claim.Owner, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task DuplicateOutboxIdRollsBackRequestedTransition()
    {
        var s = new Setup();
        await s.Prepare();
        await s.Source.Store.UpdateWithOutboxAsync(s.Id, _ => (true, OutboxMessage.FromInvocation(s.Request)));
        var before = await s.State();

        await Assert.ThrowsAsync<IOException>(() => s.Publisher.PublishAsync(s.Id, s.Request.Identity.InvocationId));

        var after = await s.State();
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(ExternalProcessStatus.Prepared, after.Steps[0].InvocationProgress!.Status);
        Assert.Null(after.Steps[0].Publication);
        Assert.Empty(s.SourceBus.Requests);
        Assert.Equal(s.Request.Message, Assert.IsType<ProcessInvocationRequest>(
            Assert.Single(await s.Source.Store.ClaimOutboxAsync("inspect", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), 2))
                .Message.ToInvocation()).Message);
    }

    [Fact]
    public async Task ConcurrentTargetDeliveriesKeepOneTargetAndOneDecision()
    {
        var s = new Setup(); await s.Prepare();
        s.TargetBus.Accept = _ => Task.CompletedTask;
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => s.TargetReceiver.ReceiveAsync(s.Request))));
        Assert.Single(await s.Target.Store.ListAsync());
        Assert.Single(s.TargetBus.Accepted.Select(a => a.TargetSagaInstanceId).Distinct());
        Assert.Single(s.TargetBus.Accepted.Select(a => a.Message.MessageId).Distinct());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentConflictingDeliveriesCannotReplaceAdmission(bool differentIdentity)
    {
        var s = new Setup();
        await s.Prepare();
        s.TargetBus.Accept = _ => Task.CompletedTask;
        var identity = s.Request.Identity;
        var conflicting = differentIdentity
            ? new ProcessInvocationRequest(new(identity.InvocationId, "other-correlation", identity.Source,
                identity.SourceSagaInstanceId, identity.SourceStep, identity.Target), s.Request.Payload)
            : new ProcessInvocationRequest(identity, System.Text.Json.JsonSerializer.SerializeToElement(new { Amount = 99 }));
        var delivered = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(async () =>
        {
            try { await s.TargetReceiver.ReceiveAsync(i % 2 == 0 ? s.Request : conflicting); return true; }
            catch (InvalidOperationException error) when (error.Message.Contains("conflict", StringComparison.OrdinalIgnoreCase) ||
                                                          error.Message.Contains("mismatch", StringComparison.OrdinalIgnoreCase)) { return false; }
        })));
        Assert.Equal(8, delivered.Count(result => result));
        var target = Assert.Single(await s.Target.Store.ListAsync());
        var decision = (await s.Target.Store.FindIncomingDecisionAsync(identity.InvocationId))!;
        Assert.Equal(decision.Request.Identity, target.IncomingInvocation!.Identity);
        Assert.Equal(decision.Request.Payload.GetRawText(), target.IncomingInvocation.Payload!.Value.GetRawText());
        Assert.Equal(decision.Reply, Assert.Single(s.TargetBus.Accepted).Message);
        Assert.Single(s.Target.Bus.Commands);
    }

    [Fact]
    public async Task TargetOwnershipAndMissingPolicyFailClosed()
    {
        var s = new Setup(); await s.Prepare();
        var id = s.Request.Identity;
        var wrong = new ProcessInvocationRequest(new(id.InvocationId, id.CorrelationId, id.Source, id.SourceSagaInstanceId, id.SourceStep,
            new(id.Target.Domain, id.Target.Process, "9.0")), s.Request.Payload);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.TargetReceiver.ReceiveAsync(wrong));
        Assert.Empty(await s.Target.Store.ListAsync());
        s.TargetBus.Reject = _ => Task.CompletedTask;
        var receiver = new ProcessInvocationReceiver(s.Target.Store, s.Target.Registry,
            new(s.Target.Store, s.Target.Registry, s.Target.Clock), s.TargetBus, s.Target.Clock, [], s.Target.Coordinator, s.Target.Dispatcher);
        await receiver.ReceiveAsync(s.Request);
        Assert.Single(s.TargetBus.Rejected);
        Assert.Empty(await s.Target.Store.ListAsync());
    }

    [Fact]
    public async Task CancellationAndTerminalSourceCannotReserveOrSend()
    {
        var s = new Setup(); await s.Prepare();
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Publisher.PublishAsync(s.Id, s.Request.Identity.InvocationId, cancellationToken: canceled.Token));
        Assert.Equal(ExternalProcessStatus.Prepared, (await s.State()).Steps[0].InvocationProgress!.Status);
        await s.Source.Store.UpdateAsync(s.Id, state => { state.Status = SagaStatus.TimedOut; state.Steps[0].Status = SagaStepStatus.TimedOut; return true; });
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Publisher.PublishAsync(s.Id, s.Request.Identity.InvocationId));
        Assert.Empty(s.SourceBus.Requests);
    }

    [Fact]
    public async Task OutboxRetainsLocalCommandAfterRemoteInvocationAndPublishFailure()
    {
        var s = new Setup();
        s.Source.Bus.OnPublish = (_, _) => throw new IOException("Participant publication failed after invocation delivery.");
        var boundary = new Boundary(async (command, ct) =>
        {
            var receipt = await s.SourceManager.PrepareAsync(command.Headers.SagaInstanceId, command.Headers.StepId, "remote", new { Amount = 42 }, ct);
            await s.Publisher.PublishAsync(command.Headers.SagaInstanceId, receipt.InvocationId, cancellationToken: ct);
        });
        var executor = new SagaStepExecutor(s.Source.Store, new CommandDispatcher(s.Source.Bus), s.Source.Clock, execution: boundary);
        var coordinator = new SagaCoordinator(s.Source.Registry, s.Source.Store, s.Source.Instances, executor, new(), s.Source.States, s.Source.Compensations, s.Source.Clock);
        await coordinator.StartAsync(new { Amount = 42 });
        var source = Assert.Single(await s.Source.Store.ListAsync());
        Assert.Equal(SagaStatus.Running, source.Status);
        var pending = Assert.Single(await s.Source.Store.ClaimOutboxAsync("inspection", DateTimeOffset.UtcNow.AddMinutes(2), TimeSpan.FromMinutes(1), 10));
        Assert.Equal(source.Steps[0].Execution!.CommandId, pending.Message.MessageId);
        var target = Assert.Single(await s.Target.Store.ListAsync());
        Assert.Equal(SagaStepStatus.Dispatched, target.Steps[0].Status);
        await s.TargetPublisher.TransitionTerminalAsync(target.Id, SagaStatus.Completed);
        var relation = await s.SourceManager.FindAsync(source.Id, source.Steps[0].Invocation!.Identity.InvocationId);
        Assert.Equal(ExternalProcessStatus.Completed, relation.LastKnownStatus);
        Assert.Null(relation.Reconciliation);
        Assert.Equal(SagaStatus.Running, (await s.Source.Store.FindAsync(source.Id))!.Status);
    }

    [Fact]
    public async Task OutOfOrderAndConflictingOutcomesDoNotCorruptState()
    {
        var s = new Setup(); await s.Prepare(); s.SourceBus.Request = _ => Task.CompletedTask;
        await s.Publisher.PublishAsync(s.Id, s.Request.Identity.InvocationId);
        var metadata = new InvocationMessage(s.Request.Identity, Guid.NewGuid().ToString("N"), s.Source.Clock.UtcNow);
        var terminal = new ProcessInvocationTerminalOutcome(metadata, "target-id", ExternalProcessStatus.Completed);
        await Assert.ThrowsAsync<InvocationOutcomeNotReadyException>(() => s.SourceReceiver.ReceiveAsync(terminal));
        var acceptance = new ProcessInvocationAccepted(new(s.Request.Identity, Guid.NewGuid().ToString("N"), s.Source.Clock.UtcNow), "target-id");
        await s.SourceReceiver.ReceiveAsync(acceptance); await s.SourceReceiver.ReceiveAsync(acceptance);
        await s.SourceReceiver.ReceiveAsync(terminal); await s.SourceReceiver.ReceiveAsync(terminal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.SourceReceiver.ReceiveAsync(new ProcessInvocationTerminalOutcome(metadata, "target-id", ExternalProcessStatus.Failed)));
        var identity = s.Request.Identity;
        var wrong = new ProcessInvocationIdentity(identity.InvocationId, "wrong-correlation", identity.Source, identity.SourceSagaInstanceId, identity.SourceStep, identity.Target);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.SourceReceiver.ReceiveAsync(new ProcessInvocationAccepted(new(wrong, Guid.NewGuid().ToString(), s.Source.Clock.UtcNow), "target-id")));
        s.TargetBus.Accept = _ => Task.CompletedTask;
        await s.TargetReceiver.ReceiveAsync(s.Request);
        var conflicting = new ProcessInvocationRequest(identity, System.Text.Json.JsonSerializer.SerializeToElement(99));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.TargetReceiver.ReceiveAsync(conflicting));
        Assert.Equal(s.Request.Payload.GetRawText(), (await s.Target.Store.FindIncomingDecisionAsync(identity.InvocationId))!.Request.Payload.GetRawText());
    }
}
