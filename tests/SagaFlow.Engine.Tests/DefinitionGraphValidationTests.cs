using SagaFlow.Core.Definitions;
using SagaFlow.Engine.ProcessDefinition;

namespace SagaFlow.Engine.Tests;

public sealed class DefinitionGraphValidationTests
{
    [Fact]
    public void LinearFlowStartsFromFirstListedStep() => Validate(
        [Step("first", "second"), Step("second", "completed")]);

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

    [Theory]
    [InlineData("completed")]
    [InlineData("compensation")]
    public void CycleWithTerminalExitIsRejected(string terminal)
    {
        var steps = new[] { Step("first", "second"), Step("second", "first", terminal) };
        var error = Assert.Throws<DefinitionValidationException>(() => Validate(steps));
        Assert.Contains("first -> second -> first", error.Message);
    }

    [Fact]
    public void SelfLoopWithTerminalExitIsRejectedWithItsName()
    {
        var error = Assert.Throws<DefinitionValidationException>(() => Validate([Step("first", "first", "completed")]));
        Assert.Contains("first -> first", error.Message);
    }

    [Fact]
    public void ThreeStepCycleIsRejectedWithAllParticipants()
    {
        var error = Assert.Throws<DefinitionValidationException>(() => Validate(
            [Step("first", "second"), Step("second", "third"), Step("third", "first", "compensation")]));
        Assert.Contains("first -> second -> third -> first", error.Message);
    }

    [Fact]
    public void ConditionalDiamondCanConvergeWithoutCreatingACycle()
    {
        var steps = new[]
        {
            new SagaStepDefinition { Name = "first", Command = "Choose", Transitions =
                [new() { Event = "success", When = "result.ready == true", NextStep = "left" },
                 new() { Event = "success", Else = true, NextStep = "right" }] },
            Step("left", "join"), Step("right", "join"), Step("join", "completed", "compensation")
        };
        Validate(steps);
    }

    [Fact]
    public void ConditionalBranchesCanTerminateIndependently() => Validate(
        [new() { Name = "first", Command = "Choose", Transitions =
            [new() { Event = "success", When = "result.ready == true", NextStep = "left" },
             new() { Event = "success", Else = true, NextStep = "right" }] },
         Step("left", "completed"), Step("right", "compensation")]);

    [Fact]
    public void UnreachableFallbackDoesNotCreateAFalseCycle() => Validate(
        [new() { Name = "first", Command = "Choose", Transitions =
            [new() { Event = "success", When = "result.ready == true", NextStep = "completed" },
             new() { Event = "success", When = "result.ready == false", NextStep = "compensation" },
             new() { Event = "success", Else = true, NextStep = "first" }] }]);

    [Theory]
    [InlineData("completed")]
    [InlineData("compensation")]
    public void SingleStepWithDirectTerminalOutcomeIsValid(string target) => Validate([Step("first", target)]);

    [Fact]
    public void EmptyStepsAreRejected() => Assert.Throws<DefinitionValidationException>(() => Validate([]));

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

    private static void Validate(SagaStepDefinition[] steps) => new ProcessDefinitionValidator().Validate(new()
    { Name = "process", Domain = "domain", Version = "1.0", Steps = steps });
}
