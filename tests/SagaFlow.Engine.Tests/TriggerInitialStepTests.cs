using SagaFlow.Engine.ProcessDefinition;

namespace SagaFlow.Engine.Tests;

public sealed class DefinitionStartTests
{
    private const string Yaml = """
        name: order-process
        version: "1.0"
        domain: order
        steps:
          - name: payment
            command: Pay
            transitions:
              - event: success
                nextStep: completed
        """;

    [Fact]
    public void FirstStepSurvivesCanonicalYamlAndRegistrySnapshot()
    {
        var codec = new DefinitionYamlCodec();
        var definition = codec.Parse(Yaml);
        var registry = new ProcessDefinitionRegistry();
        registry.RegisterLocalBatch([definition]);
        var roundTrip = codec.Parse(codec.Serialize(registry.Find(new("order", "order-process", "1.0"))!.Definition));
        Assert.Equal("payment", roundTrip.Steps[0].Name);
    }

    [Theory]
    [InlineData("trigger:\n  event: OrderCreated")]
    [InlineData("initialStep: payment")]
    public void RemovedStartFieldsAreRejected(string removedField)
    {
        var yaml = Yaml.ReplaceLineEndings("\n").Replace("domain: order", $"domain: order\n{removedField}");
        Assert.Throws<DefinitionValidationException>(() => new DefinitionYamlCodec().Parse(yaml));
    }
}
