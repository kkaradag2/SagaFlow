using System.Text.Json;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.State;
using SagaFlow.Engine.Persistence.InMemory;

namespace SagaFlow.Engine.Tests;

public sealed class InMemorySagaStateStoreTests
{
    [Fact]
    public async Task CreateFindPreservesDynamicStateAndJsonBeyondSourceDocumentLifetime()
    {
        ISagaStateStore store = new InMemorySagaStateStore();
        var now = DateTimeOffset.UtcNow;
        using var document = JsonDocument.Parse("{\"amount\":17}");
        var instance = new SagaInstance
        {
            DefinitionName = "process", DefinitionDomain = "order", DefinitionVersion = "1.0", CorrelationId = "correlation",
            Context = document.RootElement, CurrentStep = "work", CreatedAt = now, UpdatedAt = now,
            Steps = [new() { StepName = "work", Command = "Work", Input = document.RootElement, Result = document.RootElement, ExecutionOrder = 1 }],
            Compensations = [new() { StepId = "work", Command = "Undo", Result = document.RootElement }]
        };
        await store.SaveAsync(instance);
        document.Dispose();
        var saved = (await store.FindAsync(Guid.Parse(instance.Id)))!;
        Assert.Equal(17, saved.Context.GetProperty("amount").GetInt32());
        Assert.Equal(17, saved.Steps[0].Input!.Value.GetProperty("amount").GetInt32());
        Assert.Equal(17, saved.Compensations[0].Result!.Value.GetProperty("amount").GetInt32());
        Assert.Equal(instance.CorrelationId, saved.CorrelationId);
        Assert.Equal(instance.CreatedAt, saved.CreatedAt);
        Assert.Equal(instance.UpdatedAt, saved.UpdatedAt);
        Assert.Equal(1, saved.Revision);
    }

    [Fact]
    public async Task StoreDoesNotExposeMutableStateThroughSaveFindOrUpdateCallbackResults()
    {
        ISagaStateStore store = new InMemorySagaStateStore();
        var original = new SagaInstance { Steps = [new() { StepName = "work" }], Compensations = [new() { StepId = "work" }] };
        await store.SaveAsync(original);
        original.Steps[0].Status = SagaStepStatus.Failed;
        var returned = (await store.FindAsync(original.Id))!;
        returned.Context = JsonSerializer.SerializeToElement("tampered");
        returned.Status = SagaStatus.Failed;
        returned.Steps[0].Status = SagaStepStatus.Completed;
        returned.Compensations[0].Status = SagaCompensationStatus.Planned;
        var callbackResult = await store.UpdateAsync(original.Id, working => working);
        callbackResult.Steps[0].Status = SagaStepStatus.Failed;
        var fresh = (await store.FindAsync(original.Id))!;
        Assert.Equal(SagaStatus.Pending, fresh.Status);
        Assert.Equal(SagaStepStatus.Pending, fresh.Steps[0].Status);
        Assert.Equal(SagaCompensationStatus.Pending, fresh.Compensations[0].Status);
        Assert.Equal(JsonValueKind.Null, fresh.Context.ValueKind);
    }

    [Fact]
    public async Task ConcurrentAtomicUpdatesDoNotLoseChanges()
    {
        ISagaStateStore store = new InMemorySagaStateStore();
        var state = new SagaInstance { Context = JsonSerializer.SerializeToElement(0) };
        await store.SaveAsync(state);
        await Task.WhenAll(Enumerable.Range(0, 500).Select(_ => Task.Run(async () => await store.UpdateAsync(state.Id, working =>
        {
            working.Context = JsonSerializer.SerializeToElement(working.Context.GetInt32() + 1);
            return true;
        }))));
        var saved = (await store.FindAsync(state.Id))!;
        Assert.Equal(500, saved.Context.GetInt32());
        Assert.Equal(501, saved.Revision);
    }

    [Fact]
    public async Task SaveRejectsStaleReadModifyWriteInsteadOfOverwritingAnotherUpdate()
    {
        ISagaStateStore store = new InMemorySagaStateStore();
        var state = new SagaInstance();
        await store.SaveAsync(state);
        var first = (await store.FindAsync(state.Id))!;
        var stale = (await store.FindAsync(state.Id))!;
        first.Status = SagaStatus.Running;
        await store.SaveAsync(first);
        stale.Status = SagaStatus.Failed;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.SaveAsync(stale));
        Assert.Equal(SagaStatus.Running, (await store.FindAsync(state.Id))!.Status);
    }

    [Fact]
    public async Task CallbackFailureAndCancellationRollBackAllChanges()
    {
        ISagaStateStore store = new InMemorySagaStateStore();
        var state = new SagaInstance();
        await store.SaveAsync(state);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.UpdateAsync<bool>(state.Id, working =>
        { working.Status = SagaStatus.Failed; throw new InvalidOperationException("abort"); }));
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.UpdateAsync(state.Id, working =>
        { working.Status = SagaStatus.Failed; cancellation.Cancel(); return true; }, cancellation.Token));
        Assert.Equal(SagaStatus.Pending, (await store.FindAsync(state.Id))!.Status);
        Assert.Equal(1, (await store.FindAsync(state.Id))!.Revision);
    }

    [Fact]
    public async Task MissingInstanceLookupIsNullAndUpdateFailsClearly()
    {
        ISagaStateStore store = new InMemorySagaStateStore();
        Assert.Null(await store.FindAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await store.UpdateAsync("unknown", _ => true));
    }

    [Fact]
    public async Task ReentrantWritesCannotSilentlyOverwriteAnInnerUpdate()
    {
        ISagaStateStore store = new InMemorySagaStateStore();
        var state = new SagaInstance();
        await store.SaveAsync(state);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.UpdateAsync(state.Id, working =>
        {
            store.UpdateAsync(state.Id, inner => { inner.Status = SagaStatus.Failed; return true; }).GetAwaiter().GetResult();
            working.Status = SagaStatus.Completed;
            return true;
        }));
        Assert.Equal(SagaStatus.Pending, (await store.FindAsync(state.Id))!.Status);
        await store.UpdateAsync(state.Id, working => { working.Status = SagaStatus.Running; return true; });
        Assert.Equal(SagaStatus.Running, (await store.FindAsync(state.Id))!.Status);
    }
}
