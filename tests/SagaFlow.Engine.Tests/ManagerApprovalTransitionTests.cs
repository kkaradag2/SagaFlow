using SagaFlow.Core.State;
using SagaFlow.Engine.ProcessDefinition;
using SagaFlow.Engine.Timeouts;

namespace SagaFlow.Engine.Tests;

public sealed class ManagerApprovalTransitionTests
{
    private static CoordinationTestFixture Fixture() => new(new DefinitionYamlCodec().Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "order-process.yaml"))));

    [Theory]
    [InlineData(999, "shipment")]
    [InlineData(1000, "shipment")]
    [InlineData(1001, "managerNotification")]
    public async Task ActualOrderYamlSelectsBranchByQuantity(long quantity, string nextStep)
    {
        var fixture = Fixture();
        var id = await fixture.Coordinator.StartAsync(new { totalQuantity = quantity });
        await Succeed(fixture);
        await Succeed(fixture);
        Assert.Equal(nextStep, (await fixture.State(id)).CurrentStep);
        Assert.Equal(nextStep, fixture.Bus.Commands.Last().Headers.StepId);
        Assert.Equal(3, fixture.Bus.Commands.Count);
    }

    [Fact]
    public async Task PendingHumanDecisionHasNoResultNoShipmentAndNoTimeoutOrMessageTtl()
    {
        var fixture = Fixture();
        var id = await fixture.Coordinator.StartAsync(new { totalQuantity = 1001 });
        await Succeed(fixture); await Succeed(fixture); await Succeed(fixture);
        var state = await fixture.State(id);
        Assert.Equal(SagaStatus.Running, state.Status);
        Assert.Equal("managerApproval", state.CurrentStep);
        var approval = state.Steps.Single(step => step.StepId == "managerApproval");
        Assert.Equal(SagaStepStatus.Dispatched, approval.Status);
        Assert.Null(approval.Result);
        Assert.Null(approval.TimeoutLimit);
        Assert.Null(fixture.Bus.Commands.Last().TimeToLive);
        Assert.DoesNotContain(fixture.Bus.Commands, command => command.Name == "CreateShipment");
        var detector = new SagaTimeoutDetector(fixture.Store, new FarFutureClock());
        Assert.Equal(0, await detector.DetectAsync());
        Assert.Equal(SagaStatus.Running, (await fixture.State(id)).Status);
        Assert.Equal(4, fixture.Bus.Commands.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SuccessfulDecisionIsStoredBeforeYamlRoutingAndRejectExecutesOnlyRealCompensations(bool approved)
    {
        var fixture = Fixture();
        var id = await fixture.Coordinator.StartAsync(new { totalQuantity = 1001 });
        await Succeed(fixture); await Succeed(fixture); await Succeed(fixture);
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.Last(), result: new { approved }));
        var state = await fixture.State(id);
        var approval = state.Steps.Single(step => step.StepId == "managerApproval");
        Assert.Equal(SagaStepStatus.Completed, approval.Status);
        Assert.Equal(approved, approval.Result!.Value.GetProperty("approved").GetBoolean());
        if (approved)
        {
            Assert.Equal("shipment", state.CurrentStep);
            Assert.Equal("CreateShipment", fixture.Bus.Commands.Last().Name);
            await Succeed(fixture); await Succeed(fixture);
            Assert.Equal(SagaStatus.Completed, (await fixture.State(id)).Status);
        }
        else
        {
            Assert.Equal(SagaStatus.Compensating, state.Status);
            Assert.Equal(new[] { "inventory", "payment" }, state.Compensations.Select(item => item.StepId));
            Assert.Equal("ReleaseInventory", fixture.Bus.Commands.Last().Name);
            await Succeed(fixture);
            Assert.Equal("RefundPayment", fixture.Bus.Commands.Last().Name);
            await Succeed(fixture);
            Assert.Equal(SagaStatus.Compensated, (await fixture.State(id)).Status);
            Assert.DoesNotContain(fixture.Bus.Commands, command => command.Name is "CreateShipment" or "CancelShipment");
        }
    }

    [Fact]
    public async Task TechnicalApprovalFailureUsesFailureTransitionAndStartsCompensation()
    {
        var fixture = Fixture();
        var id = await fixture.Coordinator.StartAsync(new { totalQuantity = 1001 });
        await Succeed(fixture); await Succeed(fixture); await Succeed(fixture);
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.Last(), success: false));
        var state = await fixture.State(id);
        Assert.Equal(SagaStepStatus.Failed, state.Steps.Single(step => step.StepId == "managerApproval").Status);
        Assert.Equal(SagaStatus.Compensating, state.Status);
        Assert.Equal(new[] { "inventory", "payment" }, state.Compensations.Select(item => item.StepId));
        Assert.Equal("ReleaseInventory", fixture.Bus.Commands.Last().Name);
    }

    private static Task Succeed(CoordinationTestFixture fixture) => fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.Last()));

    private sealed class FarFutureClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddYears(1);
    }
}
