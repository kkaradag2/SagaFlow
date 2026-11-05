using System.Text.Json;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Definitions;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Engine.Coordination;
using SagaFlow.Engine.Messaging;
using SagaFlow.Engine.ProcessDefinition;
namespace SagaFlow.Engine.Tests;

public sealed partial class CrossDomainCoordinationTests
{
    private sealed class Port : IProcessInvocationBus
    {
        public Func<ProcessInvocationRequest, Task> Request = _ => Task.CompletedTask;
        public Func<ProcessInvocationAccepted, Task> Accepted = _ => Task.CompletedTask;
        public Func<ProcessInvocationRejected, Task> Rejected = _ => Task.CompletedTask;
        public Func<ProcessInvocationTerminalOutcome, Task> Terminal = _ => Task.CompletedTask;
        public async ValueTask PublishAsync(ProcessInvocationRequest value, CancellationToken ct = default) => await Request(value);
        public async ValueTask PublishAsync(ProcessInvocationAccepted value, CancellationToken ct = default) => await Accepted(value);
        public async ValueTask PublishAsync(ProcessInvocationRejected value, CancellationToken ct = default) => await Rejected(value);
        public async ValueTask PublishAsync(ProcessInvocationTerminalOutcome value, CancellationToken ct = default) => await Terminal(value);
    }
    private sealed class Policy(string? reason = null) : IProcessInvocationPolicy
    {
        public ValueTask<string?> ValidateAsync(ProcessInvocationRequest request, CancellationToken ct) => ValueTask.FromResult<string?>(reason);
    }
    private sealed class Boundary(Func<SagaCommand, CancellationToken, Task> execute) : ILocalSagaExecutionBoundary
    {
        public Task ExecuteAsync(SagaCommand command, CancellationToken ct) => execute(command, ct);
    }
    private sealed class Setup
    {
        public Port SourcePort { get; } = new(); public Port TargetPort { get; } = new();
        public CoordinationTestFixture Source { get; } public CoordinationTestFixture Target { get; }
        public ProcessInvocationReceiver SourceReceiver { get; } public ProcessInvocationReceiver TargetReceiver { get; }
        public ProcessInvocationManager SourceManager { get; } public ProcessInvocationPublisher SourcePublisher { get; }
        public ProcessInvocationPublisher TargetPublisher { get; }
        public Setup(bool reject = false)
        {
            var source = new SagaDefinition
            {
                Domain = "source", Name = "local", Version = "1.0",
                BigPicture = [new() { Domain = "target", Process = "remote", Version = "1.0", Endpoint = "https://discovery", Relationship = "invokes" }],
                Steps = [new() { Name = "invoke", Command = "InvokeRemote", External = true,
                        Transitions = [new() { Event = "success", NextStep = "success" }, new() { Event = "failure", NextStep = "failure" }] },
                    new() { Name = "success", Command = "Continue", Transitions = [new() { Event = "success", NextStep = "completed" }] },
                    new() { Name = "failure", Command = "Recover", Transitions = [new() { Event = "success", NextStep = "completed" }] }]
            };
            var target = CoordinationTestFixture.Linear(2, name: "remote");
            target = new SagaDefinition { Domain = "target", Name = target.Name, Version = target.Version, Steps = target.Steps };
            Source = new(source, processBus: SourcePort, execution: new Boundary(async (command, ct) =>
            {
                if (command.Name != "InvokeRemote") return;
                var receipt = await SourceManager!.PrepareAsync(command.Headers.SagaInstanceId, command.Headers.StepId, "remote", command.Payload, ct);
                await SourcePublisher!.PublishAsync(command.Headers.SagaInstanceId, receipt.InvocationId, cancellationToken: ct);
            }));
            Target = new(target, processBus: TargetPort);
            SourceManager = new(Source.Store, Source.Registry, Source.Clock);
            SourcePublisher = new(Source.Store, SourcePort, Source.Registry, Source.Clock);
            TargetPublisher = new(Target.Store, TargetPort, Target.Registry, Target.Clock);
            SourceReceiver = new(Source.Store, Source.Registry, SourceManager, SourcePort, Source.Clock, [], Source.Coordinator);
            TargetReceiver = new(Target.Store, Target.Registry, new(Target.Store, Target.Registry, Target.Clock), TargetPort, Target.Clock, [new Policy(reject ? "Business rejection" : null)], Target.Coordinator);
            SourcePort.Request = request => TargetReceiver.ReceiveAsync(request);
            TargetPort.Accepted = value => SourceReceiver.ReceiveAsync(value);
            TargetPort.Rejected = value => SourceReceiver.ReceiveAsync(value);
            TargetPort.Terminal = value => SourceReceiver.ReceiveAsync(value);
        }
        public async Task<SagaInstance> Start() => await Source.State(await Source.Coordinator.StartAsync(new { value = 42 }));
        public async Task Finish(ExternalProcessStatus status)
        {
            await Target.Receiver.ReceiveAsync(Target.Outcome(Target.Bus.Commands.First()));
            var second = Target.Bus.Commands.Last();
            await Target.Receiver.ReceiveAsync(Target.Outcome(second, status == ExternalProcessStatus.Completed));
            if (status != ExternalProcessStatus.Completed)
                await Target.Receiver.ReceiveAsync(Target.Outcome(Target.Bus.Commands.Last(), status == ExternalProcessStatus.Compensated));
        }
    }

    [Theory]
    [InlineData(ExternalProcessStatus.Completed, "Continue")]
    [InlineData(ExternalProcessStatus.Compensated, "Recover")]
    [InlineData(ExternalProcessStatus.Failed, "Recover")]
    public async Task RealLocalOutcomesAutomaticallyContinueSourceThroughYaml(ExternalProcessStatus status, string nextCommand)
    {
        var s = new Setup(); var source = await s.Start();
        Assert.Empty(s.Source.Bus.Commands); // external waits without a participant command
        Assert.Equal(SagaStepStatus.Dispatched, source.Steps[0].Status);
        Assert.Single(s.Target.Bus.Commands);
        await Assert.ThrowsAsync<InvalidSagaOutcomeException>(() => s.Source.Coordinator.HandleOutcomeAsync(new()
        { Headers = new() { SagaInstanceId = source.Id, StepId = "invoke", CorrelationId = source.CorrelationId }, IsSuccess = true, Result = JsonSerializer.SerializeToElement(new { }) }));
        await s.Finish(status);
        var target = Assert.Single(await s.Target.Store.ListAsync());
        Assert.Equal(status.ToString(), target.Status.ToString());
        var after = (await s.Source.Store.FindAsync(source.Id))!;
        Assert.Equal(status, after.Steps[0].InvocationProgress!.Status);
        Assert.Null(after.Steps[0].Result);
        Assert.NotNull(after.Steps[0].CompletedAt);
        Assert.Equal(status == ExternalProcessStatus.Completed ? SagaStepStatus.Completed : SagaStepStatus.Failed, after.Steps[0].Status);
        Assert.Equal(nextCommand, Assert.Single(s.Source.Bus.Commands).Name);
        var terminal = target.InvocationTerminalOutcome!;
        Assert.Equal(terminal.Message.MessageId, s.Source.Bus.Commands.Single().Headers.CausationId);
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => s.SourceReceiver.ReceiveAsync(terminal)));
        Assert.Single(s.Source.Bus.Commands);
        await s.Source.Receiver.ReceiveAsync(s.Source.Outcome(s.Source.Bus.Commands.Single()));
        Assert.Equal(SagaStatus.Completed, (await s.Source.Store.FindAsync(source.Id))!.Status);
    }

    [Fact]
    public async Task ConcurrentDuplicateRequestsClaimOneInitialDispatch()
    {
        var s = new Setup(); s.SourcePort.Request = _ => Task.CompletedTask;
        var source = await s.Start(); var request = source.Steps[0].Invocation!;
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => s.TargetReceiver.ReceiveAsync(request))));
        Assert.Single(await s.Target.Store.ListAsync()); Assert.Single(s.Target.Bus.Commands);
    }

    [Fact]
    public async Task InitialTargetPublishFailureIsTerminalAndDuplicatesDoNotRestartIt()
    {
        var s = new Setup(); s.SourcePort.Request = _ => Task.CompletedTask;
        var source = await s.Start(); var request = source.Steps[0].Invocation!;
        s.Target.Bus.OnPublish = (_, _) => throw new IOException("initial target command publish failed");
        await Assert.ThrowsAsync<SagaDispatchException>(() => s.TargetReceiver.ReceiveAsync(request));
        var target = Assert.Single(await s.Target.Store.ListAsync()); Assert.Equal(SagaStatus.Failed, target.Status);
        Assert.Equal(ExternalProcessStatus.Failed, (await s.Source.Store.FindAsync(source.Id))!.Steps[0].InvocationProgress!.Status);
        s.Target.Bus.OnPublish = null;
        await s.TargetReceiver.ReceiveAsync(request);
        Assert.Empty(s.Target.Bus.Commands); Assert.Single(s.Source.Bus.Commands);
        Assert.Equal(target.Id, Assert.Single(await s.Target.Store.ListAsync()).Id);
    }

    [Fact]
    public async Task TerminalPublishFailurePreservesTargetAndDuplicateOutcomeRetriesStableMessage()
    {
        var s = new Setup(); var source = await s.Start();
        await s.Target.Receiver.ReceiveAsync(s.Target.Outcome(s.Target.Bus.Commands.First()));
        var last = s.Target.Bus.Commands.Last();
        s.TargetPort.Terminal = _ => throw new IOException("terminal confirmation lost");
        await Assert.ThrowsAsync<IOException>(() => s.Target.Receiver.ReceiveAsync(s.Target.Outcome(last)));
        var target = Assert.Single(await s.Target.Store.ListAsync()); Assert.Equal(SagaStatus.Completed, target.Status);
        var message = target.InvocationTerminalOutcome!;
        s.TargetPort.Terminal = value => s.SourceReceiver.ReceiveAsync(value);
        await Assert.ThrowsAsync<DuplicateSagaOutcomeException>(() => s.Target.Receiver.ReceiveAsync(s.Target.Outcome(last)));
        await s.TargetPublisher.PublishTerminalAsync(target.Id);
        Assert.Equal(message, (await s.Target.Store.FindAsync(target.Id))!.InvocationTerminalOutcome);
        Assert.Single(s.Source.Bus.Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrReconciliationSourceRetainsRemoteObservationWithoutContinuing(bool openReconciliation)
    {
        var s = new Setup(); var source = await s.Start();
        await s.Source.Store.UpdateAsync(source.Id, state =>
        {
            if (openReconciliation) state.Steps[0].Reconciliation = new("manual review", s.Source.Clock.UtcNow);
            else { state.Status = SagaStatus.Failed; state.Steps[0].Status = SagaStepStatus.Failed; }
            return true;
        });
        await s.Finish(ExternalProcessStatus.Completed);
        var after = (await s.Source.Store.FindAsync(source.Id))!;
        Assert.Equal(ExternalProcessStatus.Completed, after.Steps[0].InvocationProgress!.Status);
        Assert.Null(after.Steps[0].Result); Assert.Empty(s.Source.Bus.Commands);
        Assert.Equal(openReconciliation ? SagaStatus.Running : SagaStatus.Failed, after.Status);
    }

    [Fact]
    public async Task BusinessRejectionContinuesExternalFailureWithoutCreatingTarget()
    {
        var s = new Setup(reject: true); var source = await s.Start();
        Assert.Empty(await s.Target.Store.ListAsync());
        Assert.Equal("Recover", Assert.Single(s.Source.Bus.Commands).Name);
        Assert.Null(source.Steps[0].Result);
        Assert.Equal(ExternalProcessStatus.Rejected, source.Steps[0].InvocationProgress!.Status);
        Assert.Equal("Business rejection", source.Steps[0].InvocationProgress!.RejectionReason);
        Assert.Equal(SagaStepStatus.Failed, source.Steps[0].Status);
    }

    [Fact]
    public async Task ConcurrentLocalFailureBetweenObservationAndContinuationCannotResurrectSource()
    {
        var s = new Setup(); var source = await s.Start();
        var target = Assert.Single(await s.Target.Store.ListAsync());
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.TargetPort.Terminal = async outcome =>
        {
            await s.SourceManager.ObserveAsync(outcome.Message.Identity, outcome.Status, outcome.TargetSagaInstanceId);
            observed.SetResult(); await resume.Task;
            await s.Source.Coordinator.HandleExternalTerminalAsync(outcome);
        };
        var finishing = s.Finish(ExternalProcessStatus.Completed);
        await observed.Task;
        await s.Source.Store.UpdateAsync(source.Id, state =>
        { state.Status = SagaStatus.Failed; state.Steps[0].Status = SagaStepStatus.Failed; state.Steps[0].Reconciliation = new("local failure", s.Source.Clock.UtcNow); return true; });
        resume.SetResult(); await finishing;
        var after = (await s.Source.Store.FindAsync(source.Id))!;
        Assert.Equal(SagaStatus.Failed, after.Status); Assert.Null(after.Steps[0].Result);
        Assert.Equal(ExternalProcessStatus.Completed, after.Steps[0].InvocationProgress!.Status);
        Assert.NotNull(after.Steps[0].Reconciliation); Assert.Empty(s.Source.Bus.Commands);
    }

    [Fact]
    public async Task TimedOutSourceKeepsCoordinationObservationWithoutBusinessResultOrContinuation()
    {
        var s = new Setup(); var source = await s.Start();
        await s.Source.Store.UpdateAsync(source.Id, instance =>
        {
            instance.Status = SagaStatus.TimedOut;
            instance.Steps[0].Status = SagaStepStatus.TimedOut;
            return true;
        });
        await s.Finish(ExternalProcessStatus.Completed);
        var after = (await s.Source.Store.FindAsync(source.Id))!;
        Assert.Equal(SagaStatus.TimedOut, after.Status);
        Assert.Equal(SagaStepStatus.TimedOut, after.Steps[0].Status);
        Assert.Null(after.Steps[0].Result);
        Assert.Equal(ExternalProcessStatus.Completed, after.Steps[0].InvocationProgress!.Status);
        Assert.Empty(s.Source.Bus.Commands);
    }

    [Fact]
    public async Task ParticipantAndCompensationKeepIndependentBusinessSchemasEvenWithCoordinationLikeFieldNames()
    {
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear(2));
        var id = await fixture.Coordinator.StartAsync(new { invoice = 17 });
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.First(), result: new { status = "business-status", invocationId = "business-field" }));
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.Last(), success: false, result: new[] { "business-error" }));
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.Last(), result: new[] { "release", "receipt" }));
        var state = await fixture.State(id);
        Assert.Equal("business-status", state.Steps[0].Result!.Value.GetProperty("status").GetString());
        Assert.Equal("business-field", state.Steps[0].Result!.Value.GetProperty("invocationId").GetString());
        Assert.Equal("business-error", state.Steps[1].Result!.Value[0].GetString());
        Assert.Equal("receipt", state.Compensations.Single().Result!.Value[1].GetString());
        Assert.Equal(SagaCompensationStatus.Completed, state.Compensations.Single().Status);
    }

    [Fact]
    public async Task MissingExternalHandlerFailsWithoutParticipantPublication()
    {
        var definition = new SagaDefinition { Domain = "source", Name = "local", Version = "1.0",
            Steps = [new() { Name = "invoke", Command = "InvokeRemote", External = true, Transitions = [new() { Event = "success", NextStep = "completed" }] }] };
        var fixture = new CoordinationTestFixture(definition);
        await Assert.ThrowsAsync<SagaDispatchException>(() => fixture.Coordinator.StartAsync(1));
        Assert.Empty(fixture.Bus.Commands);
        Assert.Equal(SagaStatus.Failed, Assert.Single(await fixture.Store.ListAsync()).Status);
    }

    [Fact]
    public void ExternalYamlSurvivesCanonicalAndSnapshotWhileLegacyDefaultsFalse()
    {
        var codec = new DefinitionYamlCodec();
        var definition = new Setup().Source.Registry.Snapshot().Single().Definition;
        var yaml = codec.Serialize(definition);
        Assert.Contains("external: true", yaml);
        Assert.True(codec.Parse(yaml).Steps[0].External);
        Assert.False(codec.Parse(yaml).Steps[1].External);
        var legacy = codec.Serialize(CoordinationTestFixture.Linear());
        Assert.DoesNotContain("external:", legacy);
    }
}
