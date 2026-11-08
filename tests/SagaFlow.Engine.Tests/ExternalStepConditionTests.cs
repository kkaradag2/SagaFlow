using System.Text.Json;
using SagaFlow.Core.Definitions;
using SagaFlow.Engine.ProcessDefinition;

namespace SagaFlow.Engine.Tests;

public sealed class ExternalStepConditionTests
{
    [Theory]
    [InlineData("result.status", false)]
    [InlineData("steps.remote.result.status", false)]
    [InlineData("steps.remote.result", false)]
    [InlineData("result.status", true)]
    public void ExternalBusinessResultReferencesAreDefinitionErrors(string path, bool legacy)
    {
        var error = Assert.Throws<DefinitionValidationException>(() => new ProcessDefinitionValidator().Validate(Definition(path, true, legacy)));
        Assert.Contains("remote", error.Message);
        Assert.Contains("business Result is null", error.Message);
    }

    [Fact]
    public void LaterParticipantCannotReadPriorExternalBusinessResult()
    {
        var definition = new SagaDefinition { Domain = "source", Name = "local", Version = "1.0", Steps = [
            new() { Name = "remote", Command = "Invoke", External = true, Transitions = [new() { Event = "success", NextStep = "local" }] },
            new() { Name = "local", Command = "Continue", Transitions = Definition("steps.remote.result.status", false).Steps[0].Transitions }
        ] };
        Assert.Throws<DefinitionValidationException>(() => new ProcessDefinitionValidator().Validate(definition));
    }

    [Theory]
    [InlineData("result.status", false, false)]
    [InlineData("result.status", false, true)]
    [InlineData("context.result.status", true, false)]
    [InlineData("context.steps.remote.result.status", true, false)]
    public void ParticipantResultsAndActualBusinessContextRemainValid(string path, bool external, bool legacy) =>
        new ProcessDefinitionValidator().Validate(Definition(path, external, legacy));

    [Fact]
    public void ExternalCanUsePriorParticipantBusinessResult()
    {
        var definition = new SagaDefinition { Domain = "source", Name = "local", Version = "1.0", Steps = [
            new() { Name = "local", Command = "Check", Transitions = [new() { Event = "success", NextStep = "remote" }] },
            Definition("steps.local.result.status", true).Steps[0]
        ] };
        new ProcessDefinitionValidator().Validate(definition);
    }

    [Fact]
    public void YamlErrorIdentifiesSourceStepAndUnavailableBusinessResult()
    {
        var error = Assert.Throws<DefinitionValidationException>(() => new DefinitionYamlCodec().Parse("""
            name: local
            domain: source
            version: '1.0'
            steps:
              - name: remote
                command: Invoke
                external: true
                transitions:
                  - on: success
                    when: { path: result.status, operator: equals, value: Completed }
                    nextStep: completed
            """, "external.yaml"));
        Assert.Contains("external.yaml", error.Message);
        Assert.Contains("result.status", error.Message);
        Assert.Contains("business Result is null", error.Message);
    }

    internal static SagaDefinition Definition(string path, bool external, bool legacy = false) => new()
    {
        Domain = "source", Name = "local", Version = "1.0", Steps = [new()
        {
            Name = "remote", Command = "Invoke", External = external, Transitions = [
                new() { Event = "success", NextStep = "completed", When = legacy ? path + " == true" : null,
                    Condition = legacy ? null : new() { Path = path, Operator = "equals", Value = JsonSerializer.SerializeToElement("Completed") } },
                new() { Event = "success", NextStep = "compensation" }, new() { Event = "failure", NextStep = "compensation" }
            ]
        }]
    };
}
