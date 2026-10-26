using SagaFlow.Core.Definitions;
using SagaFlow.Core.State;
using SagaFlow.Engine.ProcessDefinition;

namespace SagaFlow.Engine.Tests;

public sealed class RuntimeTransitionTests
{
    [Theory]
    [InlineData("result.ready == true", true, true)]
    [InlineData("result.ready == true", false, false)]
    [InlineData("result.ready != false", true, true)]
    [InlineData("result.ready != false", false, false)]
    [InlineData("result.nested.ready == false", false, true)]
    [InlineData("result.nested.ready == false", true, false)]
    public async Task ValidatedYamlConditionsEvaluateWithTheSameRuntimeContract(string expression, bool ready, bool matches)
    {
        var codec = new DefinitionYamlCodec();
        var yaml = $$"""
            name: condition-process
            domain: order
            version: '1.0'
            steps:
              - name: work
                command: Work
                transitions:
                  - event: success
                    else: true
                    nextStep: compensation
                  - event: success
                    when: '{{expression}}'
                    nextStep: completed
            bigPicture: []
            """;
        var fixture = new CoordinationTestFixture(codec.Parse(yaml));
        var id = await fixture.Coordinator.StartAsync("payload");
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.Single(), result: new { ready, nested = new { ready } }));
        Assert.Equal(matches ? SagaStatus.Completed : SagaStatus.CompensationRequested, (await fixture.State(id)).Status);
        Assert.Single(fixture.Bus.Commands);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task ConditionalMatchAndElseSelectOnlyOneBranchIndependentlyOfDeclarationOrder(bool available, bool elseFirst)
    {
        var definition = Branch(elseFirst);
        var fixture = new CoordinationTestFixture(definition);
        var id = await fixture.Coordinator.StartAsync(new { customer = 17 });
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.Single(), result: new { flags = new { available } }));
        var state = await fixture.State(id);
        Assert.Equal(available ? "yes" : "no", state.CurrentStep);
        Assert.Equal(SagaStepStatus.Pending, state.Steps.Single(s => s.StepId == (available ? "no" : "yes")).Status);
        Assert.Equal(2, fixture.Bus.Commands.Count);
        var next = fixture.Bus.Commands.ToArray()[1];
        Assert.Equal(available ? "DoYes" : "DoNo", next.Name);
        Assert.Equal(17, next.Payload.GetProperty("customer").GetInt32());
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(next));
        Assert.Equal(SagaStatus.Completed, (await fixture.State(id)).Status);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"flags\":{}}")]
    [InlineData("{\"flags\":{\"available\":\"false\"}}")]
    [InlineData("{\"flags\":{\"available\":0}}")]
    [InlineData("{\"flags\":{\"available\":null}}")]
    [InlineData("{\"flags\":[]}")]
    public async Task MissingOrNonBooleanResultIsRejectedWithoutSilentlyTakingElse(string result)
    {
        var fixture = new CoordinationTestFixture(Branch(false));
        var id = await fixture.Coordinator.StartAsync("payload");
        using var document = System.Text.Json.JsonDocument.Parse(result);
        var outcome = fixture.Outcome(fixture.Bus.Commands.Single(), result: document.RootElement);
        var before = await fixture.State(id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Receiver.ReceiveAsync(outcome));
        var after = await fixture.State(id);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(SagaStepStatus.Dispatched, after.Steps.Single(s => s.StepId == "choose").Status);
        Assert.Single(fixture.Bus.Commands);
        // A rejected malformed result does not consume the legitimate outcome slot.
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.Single(), result: new { flags = new { available = false } }));
        Assert.Equal("no", (await fixture.State(id)).CurrentStep);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ComplementaryBooleanConditionsUseEqualityAndInequalityCorrectly(bool available)
    {
        var baseDefinition = Branch(false);
        var choose = new SagaStepDefinition { Name = "choose", Command = "Choose", Transitions =
        [new() { Event = "success", When = "result.flags.available != false", NextStep = "yes" },
         new() { Event = "success", When = "result.flags.available != true", NextStep = "no" }] };
        var definition = new SagaDefinition { Name = baseDefinition.Name, Domain = baseDefinition.Domain, Version = baseDefinition.Version,
            Steps = [choose, ..baseDefinition.Steps.Where(s => s.Name != "choose")] };
        var fixture = new CoordinationTestFixture(definition);
        var id = await fixture.Coordinator.StartAsync("payload");
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.Single(), result: new { flags = new { available } }));
        Assert.Equal(available ? "yes" : "no", (await fixture.State(id)).CurrentStep);
    }

    [Fact]
    public async Task FailureUsesItsOwnEventCategoryAndRequestsCompensationWithoutPublishingIt()
    {
        var fixture = new CoordinationTestFixture(Branch(true));
        var id = await fixture.Coordinator.StartAsync("payload");
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.Single(), success: false));
        Assert.Equal(SagaStatus.CompensationRequested, (await fixture.State(id)).Status);
        Assert.Single(fixture.Bus.Commands);
    }

    [Fact]
    public async Task ADispatchedStepCannotBeExecutedAgainEvenBeforeItsOutcome()
    {
        var fixture = new CoordinationTestFixture(Branch(true));
        var id = await fixture.Coordinator.StartAsync("payload");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Executor.ExecuteAsync(id.ToString("N")));
        Assert.Single(fixture.Bus.Commands);
    }

    [Fact]
    public async Task SuccessfulStepCanRequestCompensationWithoutLosingItsCompletedHistory()
    {
        var definition = new SagaDefinition
        {
            Name = "process", Domain = "order", Version = "1.0",
            Steps = [new() { Name = "choose", Command = "Choose", Compensation = new() { Command = "UndoChoose" }, Transitions =
                [new() { Event = "success", When = "result.accepted == true", NextStep = "completed" },
                 new() { Event = "success", Else = true, NextStep = "compensation" }] }]
        };
        var fixture = new CoordinationTestFixture(definition);
        var id = await fixture.Coordinator.StartAsync("payload");
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.Single(), result: new { accepted = false }));
        var state = await fixture.State(id);
        Assert.Equal(SagaStatus.CompensationRequested, state.Status);
        Assert.Equal(SagaStepStatus.Completed, state.Steps.Single().Status);
        Assert.Equal("UndoChoose", Assert.Single(fixture.States.GetCompensationPlan(state)).Command);
        Assert.Single(fixture.Bus.Commands);
    }

    [Fact]
    public async Task MissingOutcomeCategoryIsRejectedRatherThanUsingAnotherCategorysFallback()
    {
        var definition = new SagaDefinition
        {
            Name = "process", Domain = "order", Version = "1.0",
            Steps = [new() { Name = "work", Command = "Work", Transitions = [new() { Event = "success", NextStep = "completed" }] }]
        };
        var fixture = new CoordinationTestFixture(definition);
        var id = await fixture.Coordinator.StartAsync("payload");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.Single(), success: false)));
        Assert.Equal(SagaStepStatus.Dispatched, (await fixture.State(id)).Steps.Single().Status);
        Assert.Single(fixture.Bus.Commands);
    }

    private static SagaDefinition Branch(bool elseFirst)
    {
        var condition = new SagaTransitionDefinition { Event = "success", When = "result.flags.available == true", NextStep = "yes" };
        var fallback = new SagaTransitionDefinition { Event = "success", Else = true, NextStep = "no" };
        var failure = new SagaTransitionDefinition { Event = "failure", NextStep = "compensation" };
        return new()
        {
            Domain = "order", Name = "branch", Version = "1.0",
            Steps = [new() { Name = "choose", Command = "Choose", Compensation = new() { Command = "UndoChoose" }, Transitions = elseFirst ? [fallback, condition, failure] : [condition, fallback, failure] },
                new() { Name = "no", Command = "DoNo", Transitions = [new() { Event = "success", NextStep = "completed" }] },
                new() { Name = "yes", Command = "DoYes", Transitions = [new() { Event = "success", NextStep = "completed" }] }]
        };
    }
}
