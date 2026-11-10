using System.Text;
using SagaFlow.Core.Messages;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.State;
using SagaFlow.Storage.InMemory;
using SagaFlow.Engine.Coordination;
using SagaFlow.Engine.Messaging;

namespace SagaFlow.Engine.Tests;

public sealed class InMemoryOutboxTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow.AddMinutes(-5);

    private static OutboxMessage Message(string sagaId, string id) => new()
    {
        MessageId = id, Name = "Inventory", Destination = "inventory", Body = Encoding.UTF8.GetBytes("{\"sku\":7}"),
        RecoverySnapshot = [1, 2, 3], Headers = new()
        {
            ProtocolVersion = 1, SagaInstanceId = sagaId, StepId = "inventory", CommandId = id,
            CommandName = "Inventory", ExecutionId = "execution", CreatedAt = Now
        }
    };

    [Fact]
    public async Task StateAndMessageCommitOrRollbackTogether()
    {
        var store = new InMemorySagaStateStore();
        var saga = new SagaInstance { Id = "saga", CreatedAt = Now, UpdatedAt = Now };
        await store.SaveAsync(saga);
        await Assert.ThrowsAsync<IOException>(async () => await store.UpdateWithOutboxAsync<int>(saga.Id, state =>
        {
            state.CurrentStep = "inventory";
            throw new IOException("before commit");
        }));
        Assert.Null((await store.FindAsync(saga.Id))!.CurrentStep);
        Assert.Empty(await store.ClaimOutboxAsync("a", Now, TimeSpan.FromMinutes(1), 10));

        await store.UpdateWithOutboxAsync(saga.Id, state =>
        {
            state.CurrentStep = "inventory";
            return (true, Message(saga.Id, "command"));
        });
        Assert.Equal("inventory", (await store.FindAsync(saga.Id))!.CurrentStep);
        Assert.Equal(2, (await store.FindAsync(saga.Id))!.Revision);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.UpdateWithOutboxAsync(saga.Id, state =>
        {
            state.CurrentStep = "wrong";
            return (true, Message(saga.Id, "command"));
        }));
        Assert.Equal("inventory", (await store.FindAsync(saga.Id))!.CurrentStep);
        Assert.Equal(2, (await store.FindAsync(saga.Id))!.Revision);
        Assert.Single(await store.ClaimOutboxAsync("a", Now, TimeSpan.FromMinutes(1), 10));
    }

    [Fact]
    public async Task ClaimsRespectOwnershipExpiryAndPublishedState()
    {
        var store = new InMemorySagaStateStore();
        await store.SaveAsync(new SagaInstance { Id = "saga", CreatedAt = Now, UpdatedAt = Now });
        await store.UpdateWithOutboxAsync("saga", state => (true, Message(state.Id, "command")));
        var first = Assert.Single(await store.ClaimOutboxAsync("a", Now, TimeSpan.FromMinutes(1), 10));
        Assert.Equal(1, first.Attempts);
        Assert.Empty(await store.ClaimOutboxAsync("b", Now.AddSeconds(30), TimeSpan.FromMinutes(1), 10));
        await Assert.ThrowsAsync<OutboxClaimLostException>(async () => await store.MarkOutboxPublishedAsync("command", "b", Now));
        var recovered = Assert.Single(await store.ClaimOutboxAsync("b", Now.AddMinutes(1), TimeSpan.FromMinutes(1), 10));
        Assert.Equal(2, recovered.Attempts);
        Assert.Equal(first.Message.Body, recovered.Message.Body);
        await store.MarkOutboxPublishedAsync("command", "b", Now.AddMinutes(1));
        Assert.Empty(await store.ClaimOutboxAsync("c", Now.AddMinutes(3), TimeSpan.FromMinutes(1), 10));
    }

    [Fact]
    public async Task ConcurrentMutationsCannotCreateTheSameLogicalCommandTwice()
    {
        var store = new InMemorySagaStateStore();
        await store.SaveAsync(new SagaInstance { Id = "saga", CreatedAt = Now, UpdatedAt = Now });
        var successes = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
        {
            try
            {
                await store.UpdateWithOutboxAsync("saga", state => (true, Message(state.Id, "same")));
                return true;
            }
            catch (InvalidOperationException) { return false; }
        })));
        Assert.Single(successes, value => value);
        Assert.Equal(2, (await store.FindAsync("saga"))!.Revision);
    }

    [Fact]
    public async Task RestartedDispatcherPublishesOriginalCommandAfterCommit()
    {
        var store = new InMemorySagaStateStore();
        await store.SaveAsync(new SagaInstance { Id = "saga", CreatedAt = Now, UpdatedAt = Now });
        await store.UpdateWithOutboxAsync("saga", state => (true, Message(state.Id, "command")));
        var bus = new RecordingBus();
        await new CommandDispatcher(bus).DispatchOutboxAsync(store, CancellationToken.None);
        var delivered = Assert.Single(bus.Commands);
        Assert.Equal("command", delivered.Headers.CommandId);
        Assert.Equal("{\"sku\":7}", delivered.Payload.GetRawText());
        Assert.Empty(await store.ClaimOutboxAsync("other", DateTimeOffset.UtcNow.AddMinutes(3), TimeSpan.FromMinutes(1), 10));
    }

    [Fact]
    public async Task LostPublishedMarkCanRepublishWithUnchangedIdentityAndBody()
    {
        var store = new InMemorySagaStateStore();
        await store.SaveAsync(new SagaInstance { Id = "saga", CreatedAt = Now, UpdatedAt = Now });
        await store.UpdateWithOutboxAsync("saga", state => (true, Message(state.Id, "command")));
        var first = Assert.Single(await store.ClaimOutboxAsync("crashed", DateTimeOffset.UtcNow,
            TimeSpan.FromTicks(1), 1));
        var bus = new RecordingBus();
        await bus.PublishOutboxAsync(first.Message);
        await new CommandDispatcher(bus).DispatchOutboxAsync(store, CancellationToken.None);
        Assert.Equal(2, bus.Commands.Count);
        Assert.Equal(bus.Commands[0].Headers, bus.Commands[1].Headers);
        Assert.Equal(bus.Commands[0].Payload.GetRawText(), bus.Commands[1].Payload.GetRawText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingConfirmationLeavesEntryPendingWithoutOrdinaryPublish(bool unsupported)
    {
        var store = new InMemorySagaStateStore();
        await store.SaveAsync(new SagaInstance { Id = "saga", CreatedAt = Now, UpdatedAt = Now });
        await store.UpdateWithOutboxAsync("saga", state => (true, Message(state.Id, "command")));
        var bus = new UnconfirmedBus(unsupported
            ? new NotSupportedException("This transport cannot confirm acceptance.")
            : new IOException("The broker confirmation was lost."));

        await new CommandDispatcher(bus).DispatchOutboxAsync(store, CancellationToken.None);

        Assert.Equal("command", Assert.Single(bus.OutboxAttempts).MessageId);
        Assert.Equal(0, bus.OrdinaryPublications);
        var pending = Assert.Single(await store.ClaimOutboxAsync("retry", DateTimeOffset.UtcNow.AddMinutes(1),
            TimeSpan.FromMinutes(1), 1));
        Assert.Equal("command", pending.Message.MessageId);
        Assert.Equal(2, pending.Attempts);
    }

    private sealed class UnconfirmedBus(Exception failure) : ISagaMessageBus
    {
        public List<OutboxMessage> OutboxAttempts { get; } = [];
        public int OrdinaryPublications { get; private set; }

        public ValueTask PublishAsync(SagaCommand command, CancellationToken cancellationToken = default)
        {
            OrdinaryPublications++;
            return ValueTask.CompletedTask;
        }

        public ValueTask PublishOutboxAsync(OutboxMessage message, CancellationToken cancellationToken = default)
        {
            OutboxAttempts.Add(message);
            return ValueTask.FromException(failure);
        }
    }

    private sealed class RecordingBus : ISagaMessageBus
    {
        public List<SagaCommand> Commands { get; } = [];
        public ValueTask PublishOutboxAsync(OutboxMessage message, CancellationToken cancellationToken = default) =>
            PublishAsync(message.ToCommand(), cancellationToken);
        public ValueTask PublishAsync(SagaCommand command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ASecondDispatcherCannotPublishBeforeLocalHandlerFinishes()
    {
        var handler = new BlockingHandler();
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear(), execution: handler);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var start = fixture.Coordinator.StartAsync(new { sku = 7 }, deadline.Token);
        try
        {
            await handler.Entered.Task.WaitAsync(deadline.Token);
            var state = Assert.Single(await fixture.Store.ListAsync(deadline.Token));
            Assert.Equal(SagaStepStatus.Dispatched, state.Steps[0].Status);
            await new CommandDispatcher(fixture.Bus).DispatchOutboxAsync(fixture.Store, deadline.Token);
            Assert.Empty(fixture.Bus.Commands);
        }
        finally { handler.Finish.TrySetResult(); }
        await start;
        Assert.Single(fixture.Bus.Commands);
    }

    private sealed class BlockingHandler : ILocalSagaExecutionBoundary
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ExecuteAsync(SagaCommand command, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            return Finish.Task.WaitAsync(cancellationToken);
        }
    }
}
