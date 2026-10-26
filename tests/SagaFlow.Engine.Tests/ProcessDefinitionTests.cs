using SagaFlow.Core.Definitions;
using SagaFlow.Engine.ProcessDefinition;

namespace SagaFlow.Engine.Tests;

public sealed class ProcessDefinitionTests
{
    private const string Valid = """
        name: order-process
        version: "1.0"
        domain: order
        steps:
          - name: payment
            command: AuthorizePayment
            timeoutLimit: 10s
            maxAttempts: 3
            compensation:
              command: RefundPayment
            transitions:
              - event: success
                when: "result.available == true"
                nextStep: shipment
              - event: success
                when: "result.available == false"
                nextStep: compensation
              - event: failure
                nextStep: compensation
              - event: timeout
                nextStep: compensation
          - name: shipment
            command: CreateShipment
            transitions:
              - event: success
                nextStep: completed
        bigPicture:
          - domain: payment
            process: payment-process
            endpoint: https://payment-service:7001
        """;

    [Fact]
    public async Task LoadsActualYamlIncludingConditionsCompensationAndBigPicture()
    {
        using var directory = new DefinitionDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "order.yaml"), Valid);
        var definition = await new YamlSagaDefinitionLoader(directory.Path).LoadAsync("order.yaml");
        Assert.Equal("order", definition.Domain);
        Assert.Equal("1.0", definition.Version);
        Assert.Equal(2, definition.Steps.Count);
        Assert.Equal("10s", definition.Steps[0].TimeoutLimit);
        Assert.Equal(3, definition.Steps[0].MaxAttempts);
        Assert.Null(definition.Steps[1].TimeoutLimit);
        Assert.Equal(1, definition.Steps[1].MaxAttempts);
        Assert.Equal("RefundPayment", definition.Steps[0].Compensation!.Command);
        Assert.Null(definition.Steps[1].Compensation);
        Assert.Equal(new BooleanCondition("result.available", true, true), ConditionParser.Parse(definition.Steps[0].Transitions[0].When!));
        Assert.Equal(new[] { "success", "success", "failure", "timeout" }, definition.Steps[0].Transitions.Select(t => t.Event));
        Assert.Equal("https://payment-service:7001", definition.BigPicture.Single().Endpoint);
        var codec = new DefinitionYamlCodec();
        var serialized = codec.Serialize(definition);
        Assert.Contains("result.available", serialized);
        var roundTrip = codec.Parse(serialized);
        Assert.Equal(definition.Steps[0].Transitions[0].When, roundTrip.Steps[0].Transitions[0].When);
        Assert.Equal(codec.Serialize(definition), codec.Serialize(roundTrip));
    }

    [Theory]
    [InlineData("name: order-process", "name:", "name")]
    [InlineData("domain: order", "domain:", "domain")]
    [InlineData("version: \"1.0\"", "version: latest", "version")]
    [InlineData("name: shipment", "name: payment", "duplicate step")]
    [InlineData("command: AuthorizePayment", "command: ''", "command")]
    [InlineData("event: failure", "event: PaymentFailed", "event")]
    [InlineData("timeoutLimit: 10s", "timeoutLimit: 0s", "timeoutLimit")]
    [InlineData("timeoutLimit: 10s", "timeoutLimit: 1.5s", "timeoutLimit")]
    [InlineData("timeoutLimit: 10s", "timeoutLimit: soon", "timeoutLimit")]
    [InlineData("maxAttempts: 3", "maxAttempts: 0", "maxAttempts")]
    [InlineData("maxAttempts: 3", "maxAttempts: -1", "maxAttempts")]
    [InlineData("nextStep: shipment", "nextStep: missing", "unknown nextStep")]
    [InlineData("command: RefundPayment", "command: ''", "compensation.command")]
    [InlineData("result.available == true", "result.available() == true", "Supported condition")]
    [InlineData("https://payment-service:7001", "ftp://payment-service", "endpoint")]
    [InlineData("https://payment-service:7001", "https://user:secret@payment-service", "endpoint")]
    [InlineData("https://payment-service:7001", "https://payment-service/private", "endpoint")]
    [InlineData("process: payment-process", "process: ''", "bigPicture")]
    [InlineData("bigPicture:", "relatedProcesses:", "invalid YAML")]
    public void RejectsInvalidDefinitionsWithContext(string oldText, string newText, string expected)
    {
        var ex = Assert.Throws<DefinitionValidationException>(() => new DefinitionYamlCodec().Parse(Valid.Replace(oldText, newText)));
        Assert.Contains(expected, ex.Message);
    }

    [Theory]
    [InlineData("1ms")]
    [InlineData("30s")]
    [InlineData("2m")]
    [InlineData("1h")]
    public void AcceptsSupportedPositiveTimeoutDurations(string timeoutLimit)
    {
        var definition = new DefinitionYamlCodec().Parse(Valid.Replace("timeoutLimit: 10s", $"timeoutLimit: {timeoutLimit}"));
        Assert.Equal(timeoutLimit, definition.Steps[0].TimeoutLimit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("steps: []")]
    [InlineData("name: one\nname: two")]
    [InlineData("steps: [null]")]
    [InlineData("steps: null")]
    [InlineData("bigPicture: null")]
    [InlineData("name: one\n---\nname: two")]
    [InlineData("[not: a: mapping")]
    public void RejectsMalformedOrIncompleteContent(string yaml) =>
        Assert.Throws<DefinitionValidationException>(() => new DefinitionYamlCodec().Parse(yaml));

    [Theory]
    [InlineData("result.available == true", true, true)]
    [InlineData(" result.available != false ", false, false)]
    [InlineData("result.nested.value == false", true, false)]
    public void ConditionGrammarProducesReusableSyntax(string text, bool equality, bool value)
    {
        var condition = ConditionParser.Parse(text);
        Assert.Equal(equality, condition.IsEquality);
        Assert.Equal(value, condition.ExpectedValue);
    }

    [Theory]
    [InlineData("result.x")]
    [InlineData("result.x == 1")]
    [InlineData("result.x == 'true'")]
    [InlineData("result.x == true || true")]
    [InlineData("result['x'] == true")]
    [InlineData("GetValue() == true")]
    public void ConditionGrammarRejectsUnsupportedExpressions(string text) => Assert.Throws<FormatException>(() => ConditionParser.Parse(text));

    [Fact]
    public async Task DirectoryLoadSupportsVersionsAndRejectsDuplicates()
    {
        using var directory = new DefinitionDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "a.yaml"), Valid);
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "b.yml"), Valid.Replace("\"1.0\"", "\"2.0\""));
        var loader = new YamlSagaDefinitionLoader();
        var definitions = await loader.LoadDirectoryAsync(directory.Path);
        Assert.Equal(new[] { "1.0", "2.0" }, definitions.Select(d => d.Version));
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "c.yaml"), Valid);
        await Assert.ThrowsAsync<DefinitionValidationException>(() => loader.LoadDirectoryAsync(directory.Path));
    }

    [Fact]
    public async Task ConfiguredLoaderRejectsPathTraversal()
    {
        using var directory = new DefinitionDirectory();
        await Assert.ThrowsAsync<DefinitionValidationException>(async () => await new YamlSagaDefinitionLoader(directory.Path).LoadAsync("../outside.yaml"));
    }

    [Fact]
    public void RegistryProtectsLocalOwnershipAndMutableCallerCollections()
    {
        var parsed = new DefinitionYamlCodec().Parse(Valid);
        var steps = parsed.Steps.ToList();
        var definition = new SagaDefinition { Name = parsed.Name, Domain = parsed.Domain, Version = parsed.Version, Steps = steps };
        var registry = new ProcessDefinitionRegistry();
        registry.RegisterLocalBatch([definition]);
        steps.Clear();
        Assert.False(registry.RegisterRemote(parsed, "https://remote"));
        var snapshot = registry.Snapshot();
        Assert.Equal(DefinitionOrigin.Local, snapshot.Single().Origin);
        Assert.Equal(2, snapshot.Single().Definition.Steps.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<SagaStepDefinition>)snapshot.Single().Definition.Steps).Clear());
        Assert.Throws<DefinitionValidationException>(() => registry.RegisterLocalBatch([parsed]));
        Assert.Single(registry.Snapshot());
    }

    [Fact]
    public void RegistryKeepsRemoteDistinctAndFailedBatchDoesNotPartiallyApply()
    {
        var definition = new DefinitionYamlCodec().Parse(Valid);
        var registry = new ProcessDefinitionRegistry();
        Assert.True(registry.RegisterRemote(definition, "https://payment"));
        Assert.Equal(DefinitionOrigin.Remote, registry.Snapshot().Single().Origin);
        registry.RegisterLocalBatch([definition]);
        Assert.Equal(DefinitionOrigin.Local, registry.Snapshot().Single().Origin);
        Assert.Throws<DefinitionValidationException>(() => registry.RegisterLocalBatch([definition, new SagaDefinition()]));
        Assert.Single(registry.Snapshot());
    }

    [Fact]
    public void RejectsNullTransitionsReservedNamesAndAmbiguousUnconditionalRules()
    {
        var codec = new DefinitionYamlCodec();
        Assert.Throws<DefinitionValidationException>(() => codec.Parse(Valid.Replace("name: shipment", "name: completed")));
        Assert.Throws<DefinitionValidationException>(() => codec.Parse(Valid.Replace("when: \"result.available == false\"", "when: \"result.available != false\"")));
        Assert.Throws<DefinitionValidationException>(() => new ProcessDefinitionValidator().Validate(new SagaDefinition
            { Name = "process", Domain = "domain", Version = "1.0", Steps = [new() { Name = "work", Command = "Work", Transitions = [null!] }] }));
    }

    private sealed class DefinitionDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("sagaflow-tests-").FullName;
        public void Dispose() => Directory.Delete(Path, true);
    }
}
