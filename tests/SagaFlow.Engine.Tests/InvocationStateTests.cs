using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Definitions;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Engine.Coordination;
using SagaFlow.Storage.InMemory;
using SagaFlow.Engine.ProcessDefinition;

namespace SagaFlow.Engine.Tests;
public sealed class InvocationStateTests
{
    private readonly InMemorySagaStateStore store = new();
    private readonly ProcessDefinitionRegistry registry = new();
    private readonly Clock clock = new();
    private sealed class Clock : ISagaClock { public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-11-04T12:00:00Z"); }
    private ProcessInvocationManager Manager => new(store, registry, clock);
    private async Task<SagaInstance> Source()
    {
        var definition = new SagaDefinition
        {
            Domain = "source", Name = "process", Version = "1.0",
            Steps = [new() { Name = "invoke", Command = "Invoke", Transitions = [new() { Event = "success", NextStep = "completed" }] }],
            BigPicture = [new() { Domain = "target", Process = "remote", Version = "1.0", Relationship = "invokes", Endpoint = "https://discovery" },
                new() { Domain = "target", Process = "other", Version = "1.0", Relationship = "invokes", Endpoint = "https://discovery" }]
        };
        if (registry.Snapshot().Count == 0) registry.RegisterLocalBatch([definition]);
        var instance = new SagaInstanceManager(clock).Create(definition, new { Order = 1 });
        instance.Steps[0].Status = SagaStepStatus.Dispatched;
        await store.SaveAsync(instance);
        return instance;
    }
    [Fact]
    public async Task ConcurrentPreparationIsAtomicAndConflictsRollback()
    {
        var source = await Source();
        var receipts = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
            await Manager.PrepareAsync(source.Id, "invoke", "remote", new { Order = 1 }, default))));
        Assert.Single(receipts.Select(r => r.InvocationId).Distinct());
        var saved = (await store.FindAsync(source.Id))!;
        Assert.Equal(ExternalProcessStatus.Prepared, saved.Steps[0].InvocationProgress!.Status);
        Assert.Null(saved.Steps[0].InvocationProgress!.LastObservedAt);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Manager.PrepareAsync(source.Id, "invoke", "remote", 2, default));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Manager.PrepareAsync(source.Id, "invoke", "other", new { Order = 1 }, default));
        Assert.Equal(saved.Revision, (await store.FindAsync(source.Id))!.Revision);
        saved.Steps[0].InvocationProgress = null;
        Assert.NotNull((await store.FindAsync(source.Id))!.Steps[0].InvocationProgress);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.SaveAsync(source));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Manager.PrepareAsync(source.Id, "wrong", "remote", 1, default));
        await store.UpdateAsync(source.Id, s => { s.Status = SagaStatus.Completed; return true; });
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Manager.PrepareAsync(source.Id, "invoke", "remote", 1, default));
    }
    [Fact]
    public async Task LifecycleBindsTargetAndRejectsConflictsWithoutAdvancingLocalSaga()
    {
        var source = await Source();
        await Manager.PrepareAsync(source.Id, "invoke", "remote", 1, default);
        var id = (await store.FindAsync(source.Id))!.Steps[0].Invocation!.Identity;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Manager.ObserveAsync(id, ExternalProcessStatus.Completed, "target-saga"));
        Assert.True(await Manager.CanPublishAsync(source.Id, id.InvocationId));
        await Manager.ObserveAsync(id, ExternalProcessStatus.Requested);
        Assert.False(await Manager.CanPublishAsync(source.Id, id.InvocationId));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Manager.ObserveAsync(id, ExternalProcessStatus.Accepted));
        clock.UtcNow = clock.UtcNow.AddSeconds(1);
        await Manager.ObserveAsync(id, ExternalProcessStatus.Accepted, "target-saga");
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Manager.ObserveAsync(id, ExternalProcessStatus.Running, "different-saga"));
        await Manager.ObserveAsync(id, ExternalProcessStatus.Running);
        var completed = await Manager.ObserveAsync(id, ExternalProcessStatus.Completed);
        clock.UtcNow = clock.UtcNow.AddSeconds(1);
        var duplicate = await Manager.ObserveAsync(id, ExternalProcessStatus.Completed, "target-saga");
        Assert.Equal(completed.UpdatedAt, duplicate.UpdatedAt);
        Assert.True(duplicate.LastObservedAt > completed.LastObservedAt);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Manager.ObserveAsync(id, ExternalProcessStatus.Failed));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Manager.ObserveAsync(id, ExternalProcessStatus.Running));
        Assert.Equal(SagaStatus.Running, (await store.FindAsync(source.Id))!.Status);
        Assert.Equal(duplicate, await Manager.FindAsync(source.Id, id.InvocationId));
        var wrong = new ProcessInvocationIdentity(id.InvocationId, "wrong", id.Source, id.SourceSagaInstanceId, id.SourceStep, id.Target);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Manager.ObserveAsync(wrong, ExternalProcessStatus.Completed));
    }
    [Theory]
    [InlineData(SagaStatus.Running, SagaStepStatus.Failed)]
    [InlineData(SagaStatus.TimedOut, SagaStepStatus.TimedOut)]
    [InlineData(SagaStatus.Completed, SagaStepStatus.Completed)]
    [InlineData(SagaStatus.Failed, SagaStepStatus.Failed)]
    public async Task PreparedIsNotPublishPermission(SagaStatus sagaStatus, SagaStepStatus stepStatus)
    {
        var source = await Source();
        var receipt = await Manager.PrepareAsync(source.Id, "invoke", "remote", 1, default);
        Assert.True(await Manager.CanPublishAsync(source.Id, receipt.InvocationId));
        await store.UpdateAsync(source.Id, state => { state.Status = sagaStatus; state.Steps[0].Status = stepStatus; return true; });
        Assert.False(await Manager.CanPublishAsync(source.Id, receipt.InvocationId));
    }

    [Theory]
    [InlineData(ExternalProcessStatus.Rejected)]
    [InlineData(ExternalProcessStatus.Failed)]
    [InlineData(ExternalProcessStatus.Compensated)]
    public async Task TerminalBranchesAndStoreRollback(ExternalProcessStatus terminal)
    {
        var source = await Source();
        await Manager.PrepareAsync(source.Id, "invoke", "remote", 1, default);
        var saved = (await store.FindAsync(source.Id))!;
        var identity = saved.Steps[0].Invocation!.Identity;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.UpdateAsync<bool>(source.Id, state =>
        {
            state.Steps[0].InvocationProgress = null;
            throw new InvalidOperationException("injected callback failure");
        }));
        Assert.Equal(saved.Revision, (await store.FindAsync(source.Id))!.Revision);
        Assert.NotNull((await store.FindAsync(source.Id))!.Steps[0].InvocationProgress);
        await Manager.ObserveAsync(identity, ExternalProcessStatus.Requested);
        if (terminal != ExternalProcessStatus.Rejected) await Manager.ObserveAsync(identity, ExternalProcessStatus.Accepted, "target-id");
        var result = await Manager.ObserveAsync(identity, terminal);
        Assert.Equal(terminal, result.LastKnownStatus);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Manager.ObserveAsync(identity, ExternalProcessStatus.Prepared));
        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await Manager.FindAsync("missing", identity.InvocationId));
        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await Manager.FindAsync(source.Id, Guid.NewGuid().ToString()));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Manager.PrepareAsync(source.Id, "invoke", "remote", 1, canceled.Token));
    }

    [Fact]
    public async Task TargetOwnsIncomingLinkAndCannotBeRebound()
    {
        var source = await Source();
        await Manager.PrepareAsync(source.Id, "invoke", "remote", 1, default);
        var identity = (await store.FindAsync(source.Id))!.Steps[0].Invocation!.Identity;
        var targetDefinition = new SagaDefinition { Domain = "target", Name = "remote", Version = "1.0",
            Steps = [new() { Name = "work", Command = "Work", Transitions = [new() { Event = "success", NextStep = "completed" }] }] };
        var targetStore = new InMemorySagaStateStore();
        var targetRegistry = new ProcessDefinitionRegistry();
        targetRegistry.RegisterLocalBatch([targetDefinition]);
        var targetManager = new ProcessInvocationManager(targetStore, targetRegistry, clock);
        var target = new SagaInstance { DefinitionDomain = "target", DefinitionName = "remote", DefinitionVersion = "1.0",
            CorrelationId = identity.CorrelationId, Status = SagaStatus.Running };
        await targetStore.SaveAsync(target);
        var incoming = await targetManager.AttachIncomingAsync(target.Id, identity, clock.UtcNow);
        Assert.Equal(incoming, await targetManager.AttachIncomingAsync(target.Id, identity, clock.UtcNow));
        Assert.NotEqual(target.Id, identity.SourceSagaInstanceId);
        Assert.Null(await targetStore.FindAsync(source.Id));
        var snapshot = (await targetStore.FindAsync(target.Id))!;
        snapshot.IncomingInvocation = null;
        Assert.NotNull((await targetStore.FindAsync(target.Id))!.IncomingInvocation);
        Assert.Null((await store.FindAsync(source.Id))!.IncomingInvocation);
        var wrong = new ProcessInvocationIdentity(Guid.NewGuid().ToString(), identity.CorrelationId, identity.Source, source.Id, "invoke", identity.Target);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await targetManager.AttachIncomingAsync(target.Id, wrong, clock.UtcNow));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Manager.AttachIncomingAsync(source.Id, identity, clock.UtcNow));
    }
}
