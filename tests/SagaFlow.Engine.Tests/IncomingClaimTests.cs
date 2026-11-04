using System.Text.Json;
using SagaFlow.Core.Definitions;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Engine.Coordination;

namespace SagaFlow.Engine.Tests;
public sealed class IncomingClaimTests
{
    private static SagaDefinition Target => new()
    {
        Domain = "target", Name = "remote", Version = "1.0",
        Steps = [new() { Name = "work", Command = "Work", Transitions = [new() { Event = "success", NextStep = "completed" }] }]
    };
    private static ProcessInvocationRequest Request(string? invocationId = null, int payload = 1, string sourceDomain = "source") =>
        new(new(invocationId ?? Guid.NewGuid().ToString("N"), "source-correlation", new(sourceDomain, "process", "1.0"),
            "source-saga", "invoke", new("target", "remote", "1.0")), JsonSerializer.SerializeToElement(payload));

    [Fact]
    public async Task NormalStartThenAttachPreservesLocalCorrelationAndPreventsRebinding()
    {
        var fixture = new CoordinationTestFixture(Target);
        var manager = new ProcessInvocationManager(fixture.Store, fixture.Registry, fixture.Clock);
        var request = Request();
        var id = await fixture.Coordinator.StartAsync(request.Payload);
        var before = await fixture.State(id);
        var incoming = await manager.AttachIncomingAsync(before.Id, request);
        var after = await fixture.State(id);
        Assert.Equal(before.CorrelationId, after.CorrelationId);
        Assert.NotEqual(incoming.Identity.CorrelationId, after.CorrelationId);
        Assert.Equal(request.Identity, incoming.Identity);
        var duplicateTarget = await fixture.Coordinator.StartAsync(request.Payload);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await manager.AttachIncomingAsync(duplicateTarget.ToString("N"), request));
        Assert.Null((await fixture.State(duplicateTarget)).IncomingInvocation);
        var wrong = new ProcessInvocationRequest(new(request.Identity.InvocationId, "other", request.Identity.Source, "source-saga", "invoke", request.Identity.Target), request.Payload);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await manager.AttachIncomingAsync(before.Id, wrong));
        var wrongTarget = new ProcessInvocationRequest(new(Guid.NewGuid().ToString(), "source-correlation", request.Identity.Source, "source-saga", "invoke", new("wrong", "remote", "1.0")), request.Payload);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await manager.AttachIncomingAsync(before.Id, wrongTarget));
    }

    [Fact]
    public async Task ConcurrentClaimsCreateOneTargetAndDetectIdentityPayloadConflicts()
    {
        var fixture = new CoordinationTestFixture(Target);
        var manager = new ProcessInvocationManager(fixture.Store, fixture.Registry, fixture.Clock);
        var request = Request();
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(async () => await manager.GetOrCreateTargetAsync(request))));
        Assert.Single(results.Select(s => s.Id).Distinct());
        Assert.Single(await fixture.Store.ListAsync());
        Assert.Empty(fixture.Bus.Commands);
        Assert.NotEqual(request.Identity.CorrelationId, results[0].CorrelationId);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await manager.GetOrCreateTargetAsync(Request(request.Identity.InvocationId, 2)));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await manager.GetOrCreateTargetAsync(Request(request.Identity.InvocationId, sourceDomain: "other")));
        var other = await manager.GetOrCreateTargetAsync(Request());
        Assert.NotEqual(results[0].Id, other.Id);
        Assert.Equal(results[0].Id, (await manager.GetOrCreateTargetAsync(request)).Id);
        var serialized = JsonSerializer.Deserialize<SagaInstance>(JsonSerializer.Serialize(results[0]))!;
        await fixture.Store.SaveAsync(serialized);
        Assert.Equal(request.Identity, (await fixture.Store.FindAsync(results[0].Id))!.IncomingInvocation!.Identity);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.Store.UpdateAsync(results[0].Id, state => { state.IncomingInvocation = null; return true; }));
    }

    [Fact]
    public async Task FactoryFailuresAndCancellationLeaveNoClaimOrInstance()
    {
        var fixture = new CoordinationTestFixture(Target);
        var request = Request();
        var time = fixture.Clock.UtcNow;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.Store.GetOrCreateIncomingAsync(request,
            () => throw new InvalidOperationException("factory failed"), time));
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await fixture.Store.GetOrCreateIncomingAsync(request, () =>
        {
            var candidate = fixture.Instances.Create(Target, request.Payload);
            cancellation.Cancel();
            return candidate;
        }, time, cancellation.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.Store.GetOrCreateIncomingAsync(request,
            () => new SagaInstance { Status = SagaStatus.Completed }, time));
        Assert.Empty(await fixture.Store.ListAsync());
        var manager = new ProcessInvocationManager(fixture.Store, fixture.Registry, fixture.Clock);
        var created = await manager.GetOrCreateTargetAsync(request);
        var again = await manager.GetOrCreateTargetAsync(request);
        Assert.Equal(created.Id, again.Id);
        var unattached = fixture.Instances.Create(Target, request.Payload);
        await fixture.Store.SaveAsync(unattached);
        var next = Request();
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await manager.AttachIncomingAsync(unattached.Id, next, canceled.Token));
        Assert.Null((await fixture.Store.FindAsync(unattached.Id))!.IncomingInvocation);
        Assert.NotNull(await manager.AttachIncomingAsync(unattached.Id, next));
    }
}
