using SagaFlow.Core.Definitions;
using SagaFlow.Engine.ProcessDefinition;

namespace SagaFlow.Engine.Tests;

public sealed class ConditionalTransitionTests
{
    [Theory]
    [InlineData("data.ready == true")]
    [InlineData("context.ready == true")]
    [InlineData("ready == true")]
    [InlineData("result == true")]
    [InlineData("Result.ready == true")]
    public void YamlRejectsUnsupportedRootsWithProcessStepAndCondition(string expression)
    {
        var codec = new DefinitionYamlCodec();
        var yaml = codec.Serialize(Definition([Conditional("result.ready == true"), Fallback()]))
            .Replace("result.ready == true", expression, StringComparison.Ordinal);
        var error = Assert.Throws<DefinitionValidationException>(() => codec.Parse(yaml));
        Assert.Contains("order/order@1.0", error.Message);
        Assert.Contains("step 'work'", error.Message);
        Assert.Contains(expression, error.Message);
    }

    [Fact]
    public void ParserOnlyChecksSyntaxWhileDefinitionValidatorEnforcesRuntimeRoot()
    {
        Assert.Equal("data.ready", ConditionParser.Parse("data.ready == true").PropertyPath);
        var error = Assert.Throws<DefinitionValidationException>(() => Validate([Conditional("data.ready == true"), Fallback()]));
        Assert.Contains("must start with 'result.'", error.Message);
    }

    public static TheoryData<string, string> ExclusiveConditions => new()
    {
        { "result.available == true", "result.available == false" },
        { "result.available != true", "result.available != false" },
        { "result.available == true", "result.available != true" }
    };

    [Theory]
    [MemberData(nameof(ExclusiveConditions))]
    public void ComplementaryBooleanConditionsAreExhaustiveInEitherOrder(string first, string second)
    {
        var rules = new[] { Conditional(first), Conditional(second) };
        Validate(rules);
        Validate(rules.Reverse().ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OneConditionWithFallbackOrUnconditionalDefaultIsValidRegardlessOfOrder(bool explicitElse)
    {
        var fallback = new SagaTransitionDefinition { Event = "success", Else = explicitElse ? true : null, NextStep = "compensation" };
        Validate([Conditional("result.available == true"), fallback]);
        Validate([fallback, Conditional("result.available == true")]);
        var codec = new DefinitionYamlCodec();
        var roundTrip = codec.Parse(codec.Serialize(Definition([Conditional("result.available == true"), fallback])));
        Assert.Equal(fallback.Else, roundTrip.Steps[0].Transitions[1].Else);
    }

    [Theory]
    [InlineData("result.available == true", "result.available != false")]
    [InlineData("result.available == false", "result.available != true")]
    [InlineData("result.available == true", " result.available==true ")]
    [InlineData("result.available == true", "result.other == false")]
    public void EquivalentOrPotentiallyOverlappingConditionsAreRejected(string first, string second)
    {
        var error = Assert.Throws<DefinitionValidationException>(() => Validate([Conditional(first), Conditional(second), Fallback()]));
        Assert.Contains("event 'success'", error.Message);
        Assert.Contains("overlapping or equivalent", error.Message);
    }

    [Fact]
    public void SingleConditionWithoutFallbackIsRejected()
    {
        var error = Assert.Throws<DefinitionValidationException>(() => Validate([Conditional("result.available == true")]));
        Assert.Contains("missing fallback", error.Message);
    }

    [Fact]
    public void DuplicateElseAndMultipleDefaultsAreRejected()
    {
        Assert.Throws<DefinitionValidationException>(() => Validate([Fallback(), Fallback()]));
        Assert.Throws<DefinitionValidationException>(() => Validate([Fallback(), new() { Event = "success", NextStep = "completed" }]));
        Assert.Throws<DefinitionValidationException>(() => Validate([new() { Event = "success", NextStep = "completed" }, new() { Event = "success", NextStep = "compensation" }]));
    }

    [Fact]
    public void ElseWithWhenOrFalseElseIsRejected()
    {
        Assert.Throws<DefinitionValidationException>(() => Validate([new() { Event = "success", Else = true, When = "result.available == true", NextStep = "completed" }]));
        Assert.Throws<DefinitionValidationException>(() => Validate([new() { Event = "success", Else = false, NextStep = "completed" }]));
    }

    [Fact]
    public void OutcomeCategoriesHaveIndependentDefaultsAndConditions()
    {
        Validate([Conditional("result.available == true"), Fallback(),
            new() { Event = "failure", When = "result.available == true", NextStep = "compensation" },
            new() { Event = "failure", Else = true, NextStep = "compensation" }]);
    }

    private static SagaTransitionDefinition Conditional(string condition) => new() { Event = "success", When = condition, NextStep = "completed" };
    private static SagaTransitionDefinition Fallback() => new() { Event = "success", Else = true, NextStep = "compensation" };
    private static void Validate(SagaTransitionDefinition[] transitions) => new ProcessDefinitionValidator().Validate(Definition(transitions));
    private static SagaDefinition Definition(SagaTransitionDefinition[] transitions) => new()
    {
        Name = "order", Domain = "order", Version = "1.0",
        Steps = [new() { Name = "work", Command = "Work", Transitions = transitions }]
    };
}
