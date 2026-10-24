using SagaFlow.Engine.ProcessDefinition;

namespace SagaFlow.Engine.Tests;

public sealed class TriggerInitialStepTests
{
    private const string Yaml = """
        name: order-process
        version: "1.0"
        domain: order
        trigger:
          event: OrderCreated
        initialStep: payment
        steps:
          - name: payment
            command: Pay
            transitions:
              - event: success
                nextStep: completed
        """;

    [Fact]
    public void ExplicitStartFieldsSurviveCanonicalYamlAndRegistrySnapshot()
    {
        var codec = new DefinitionYamlCodec();
        var definition = codec.Parse(Yaml);
        var registry = new ProcessDefinitionRegistry();
        registry.RegisterLocalBatch([definition]);
        var roundTrip = codec.Parse(codec.Serialize(registry.Find(new("order", "order-process", "1.0"))!.Definition));
        Assert.Equal("OrderCreated", roundTrip.Trigger.Event);
        Assert.Equal("payment", roundTrip.InitialStep);
    }

    [Theory]
    [InlineData("trigger:\n  event: OrderCreated\n", "", "trigger.event")]
    [InlineData("event: OrderCreated", "event: ''", "trigger.event")]
    [InlineData("trigger:\n  event: OrderCreated", "trigger: null", "trigger.event")]
    [InlineData("initialStep: payment", "initialStep: unknown", "initialStep 'unknown'")]
    [InlineData("initialStep: payment", "", "initialStep")]
    public void MissingOrInvalidStartFieldsAreRejectedWithoutInferringListOrder(string before, string after, string expected)
    {
        var error = Assert.Throws<DefinitionValidationException>(() => new DefinitionYamlCodec().Parse(Yaml.Replace(before, after)));
        Assert.Contains(expected, error.Message);
    }
}
