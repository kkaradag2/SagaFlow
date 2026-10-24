using SagaFlow.Core.Definitions;
using SagaFlow.Engine.ProcessDefinition;

namespace SagaFlow.Engine.Tests;

public sealed class DefinitionGraphValidationTests
{
    [Fact]
    public void LinearFlowIsReachableFromExplicitInitialStepEvenWhenListedLast() => Validate(
        [Step("second", "completed"), Step("first", "second")], "first");

    [Fact]
    public void BranchingFlowCanTerminateThroughEitherReservedTarget() => Validate(
        [Step("first", "second", "compensation"), Step("second", "completed")]);

    [Fact]
    public void UnreachableStepIsRejectedWithItsName()
    {
        var error = Assert.Throws<DefinitionValidationException>(() => Validate([Step("first", "completed"), Step("orphan", "completed")]));
        Assert.Contains("step 'orphan' is unreachable", error.Message);
    }

    [Fact]
    public void CycleWithoutTerminalPathIsRejected()
    {
        var error = Assert.Throws<DefinitionValidationException>(() => Validate([Step("first", "second"), Step("second", "first")]));
        Assert.Contains("no reachable terminal path", error.Message);
        Assert.Contains("step 'second' cannot reach", error.Message);
    }

    [Fact]
    public void SelfLoopWithoutExitIsRejected() => Assert.Throws<DefinitionValidationException>(() => Validate([Step("first", "first")]));

    [Fact]
    public void CycleWithTerminalExitIsAllowed() => Validate([Step("first", "second"), Step("second", "first", "completed")]);

    [Theory]
    [InlineData("completed")]
    [InlineData("compensation")]
    public void SingleStepWithDirectTerminalOutcomeIsValid(string target) => Validate([Step("first", target)]);

    [Fact]
    public void InvalidInitialStepIsRejected() => Assert.Throws<DefinitionValidationException>(() => Validate([Step("first", "completed")], "unknown"));

    [Fact]
    public void ClosedBranchIsRejectedEvenWhenAnotherBranchCanComplete()
    {
        var error = Assert.Throws<DefinitionValidationException>(() => Validate([Step("first", "loop", "completed"), Step("loop", "loop")]));
        Assert.Contains("step 'loop' cannot reach", error.Message);
    }

    [Fact]
    public void UnreachableDefaultAfterExhaustiveConditionsDoesNotSupplyATerminalExit()
    {
        var step = new SagaStepDefinition
        {
            Name = "first", Command = "Work", Transitions =
            [
                new() { Event = "success", When = "result.ready == true", NextStep = "first" },
                new() { Event = "success", When = "result.ready == false", NextStep = "first" },
                new() { Event = "success", Else = true, NextStep = "completed" }
            ]
        };
        Assert.Throws<DefinitionValidationException>(() => Validate([step]));
    }

    private static SagaStepDefinition Step(string name, string successTarget, string? failureTarget = null) => new()
    {
        Name = name, Command = "Work", Transitions = failureTarget is null
            ? [new() { Event = "success", NextStep = successTarget }]
            : [new() { Event = "success", NextStep = successTarget }, new() { Event = "failure", NextStep = failureTarget }]
    };

    private static void Validate(SagaStepDefinition[] steps, string initialStep = "first") => new ProcessDefinitionValidator().Validate(new()
    { Name = "process", Domain = "domain", Version = "1.0", Trigger = new() { Event = "Created" }, InitialStep = initialStep, Steps = steps });
}
