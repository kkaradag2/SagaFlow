using System.Text.Json;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Definitions;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Engine.Coordination;
using SagaFlow.Engine.Messaging;

namespace SagaFlow.Engine.Tests;

public sealed class IncomingInvocationInitializationTests
{
    private sealed class Policy(bool rejected = false) : IProcessInvocationPolicy
    { public ValueTask<string?> ValidateAsync(ProcessInvocationRequest request, CancellationToken ct) => ValueTask.FromResult(rejected ? "Business rejected" : (string?)null); }
    private sealed class Initializer : IIncomingInvocationInitializer
    {
        public int Calls; public bool Fail; public Func<Task>? Wait;
        public async ValueTask<JsonElement> InitializeAsync(ProcessInvocationRequest request, string id, CancellationToken ct)
        { Interlocked.Increment(ref Calls); if (Wait is not null) await Wait(); if (Fail) throw new IOException("Application initialization failed"); return JsonSerializer.SerializeToElement(new { businessId = "owner-generated", targetId = id }); }
    }
    private sealed class Bus : IProcessInvocationBus
    {
        public int Accepted, Rejected;
        public ValueTask PublishAsync(ProcessInvocationRequest r, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask PublishAsync(ProcessInvocationAccepted r, CancellationToken ct = default) { Interlocked.Increment(ref Accepted); return ValueTask.CompletedTask; }
        public ValueTask PublishAsync(ProcessInvocationRejected r, CancellationToken ct = default) { Interlocked.Increment(ref Rejected); return ValueTask.CompletedTask; }
        public ValueTask PublishAsync(ProcessInvocationTerminalOutcome r, CancellationToken ct = default) => ValueTask.CompletedTask;
    }
    private static (CoordinationTestFixture Target, ProcessInvocationReceiver Receiver, ProcessInvocationRequest Request, Bus Bus) Setup(Initializer initializer, bool rejected = false)
    {
        var definition = new SagaDefinition { Domain = "target", Name = "process", Version = "1.0", Steps = [new() { Name = "work", Command = "Work", Transitions = [new() { Event = "success", NextStep = "completed" }] }] };
        var fixture = new CoordinationTestFixture(definition); var bus = new Bus();
        var receiver = new ProcessInvocationReceiver(fixture.Store, fixture.Registry, new(fixture.Store, fixture.Registry, fixture.Clock), bus,
            fixture.Clock, [new Policy(rejected)], fixture.Coordinator, initializer);
        var request = new ProcessInvocationRequest(new(Guid.NewGuid().ToString("N"), "source-correlation", new("source", "caller", "1.0"),
            "source-saga", "invoke", new("target", "process", "1.0")), JsonSerializer.SerializeToElement(new { original = true }));
        return (fixture, receiver, request, bus);
    }
    [Fact]
    public async Task InitializedPayloadDispatchesWhileOriginalRequestAndLocalIdentityArePreserved()
    {
        var initializer = new Initializer(); var setup = Setup(initializer);
        await setup.Receiver.ReceiveAsync(setup.Request);
        var state = Assert.Single(await setup.Target.Store.ListAsync()); var command = Assert.Single(setup.Target.Bus.Commands);
        Assert.Equal("owner-generated", command.Payload.GetProperty("businessId").GetString());
        Assert.Equal(state.Id, command.Payload.GetProperty("targetId").GetString());
        Assert.Equal(setup.Request.Payload.GetRawText(), state.IncomingInvocation!.Payload!.Value.GetRawText());
        Assert.NotEqual("source-correlation", state.CorrelationId); Assert.Equal(1, setup.Bus.Accepted);
        await setup.Receiver.ReceiveAsync(setup.Request);
        Assert.Equal(1, initializer.Calls); Assert.Single(setup.Target.Bus.Commands);
        Assert.Equal(state.Context.GetRawText(), (await setup.Target.Store.FindAsync(state.Id))!.Context.GetRawText());
    }
    [Fact]
    public async Task BusinessRejectionDoesNotInitializeOrCreateTarget()
    {
        var initializer = new Initializer(); var setup = Setup(initializer, true);
        await setup.Receiver.ReceiveAsync(setup.Request);
        Assert.Equal(0, initializer.Calls); Assert.Equal(1, setup.Bus.Rejected); Assert.Empty(await setup.Target.Store.ListAsync());
    }
    [Fact]
    public async Task InitializationFailureWithholdsAcceptanceAndDispatchAndCanRetryTheSameClaim()
    {
        var initializer = new Initializer { Fail = true }; var setup = Setup(initializer);
        await Assert.ThrowsAsync<IOException>(() => setup.Receiver.ReceiveAsync(setup.Request));
        var pending = Assert.Single(await setup.Target.Store.ListAsync()); Assert.Equal(SagaStepStatus.Pending, pending.Steps[0].Status);
        Assert.Equal(0, setup.Bus.Accepted); Assert.Empty(setup.Target.Bus.Commands);
        initializer.Fail = false; await setup.Receiver.ReceiveAsync(setup.Request);
        Assert.Equal(pending.Id, Assert.Single(await setup.Target.Store.ListAsync()).Id); Assert.Single(setup.Target.Bus.Commands);
    }
    [Fact]
    public async Task ConcurrentDuplicateInitializersKeepOneTargetAndOneInitialDispatch()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var count = 0;
        var initializer = new Initializer { Wait = () => { if (Interlocked.Increment(ref count) == 2) entered.TrySetResult(); return release.Task; } };
        var setup = Setup(initializer);
        var first = setup.Receiver.ReceiveAsync(setup.Request); var second = setup.Receiver.ReceiveAsync(setup.Request);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); release.TrySetResult(); await Task.WhenAll(first, second);
        Assert.Single(await setup.Target.Store.ListAsync()); Assert.Single(setup.Target.Bus.Commands); Assert.Equal(2, setup.Bus.Accepted);
    }
}
