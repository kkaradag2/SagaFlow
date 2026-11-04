using SagaFlow.Core.Definitions;
using SagaFlow.Core.State;
using SagaFlow.Engine.Coordination;
using SagaFlow.Engine.ProcessDefinition;

namespace SagaFlow.Engine.Tests;

public sealed class ProcessInvocationContractTests
{
    private static SagaDefinition Definition(params BigPictureSourceDefinition[] targets) => new()
    {
        Domain = "order", Name = "order-process", Version = "1.0", BigPicture = targets,
        Steps = [new() { Name = "payment", Command = "Pay", Transitions = [new() { Event = "success", NextStep = "completed" }] }]
    };
    private static BigPictureSourceDefinition Target(string domain = "return", string? version = "2.0", string relationship = "invokes") =>
        new() { Domain = domain, Process = "return-process", Version = version, Endpoint = "https://return-service:7001", Relationship = relationship };
    private static SagaInstance Instance(string id) => new()
    {
        Id = id, DefinitionDomain = "order", DefinitionName = "order-process", DefinitionVersion = "1.0",
        CorrelationId = "trace-1", Status = SagaStatus.Running, CurrentStep = "payment",
        Steps = [new() { StepName = "payment", Status = SagaStepStatus.Dispatched }]
    };

    [Fact]
    public void ResolvesSourceTargetAndDynamicPayloadWithoutSharingSagaIds()
    {
        var registry = new ProcessDefinitionRegistry();
        registry.RegisterLocalBatch([Definition(Target())]);
        var request = new ProcessInvocationResolver(registry).Create(Instance("source-saga"), "return-process", new { OrderId = 42, Items = new[] { "a" } });
        Assert.Equal("source-saga", request.Identity.SourceSagaInstanceId);
        Assert.Equal("payment", request.Identity.SourceStep);
        Assert.Equal("trace-1", request.Identity.CorrelationId);
        Assert.Equal("order", request.Identity.Source.Domain);
        Assert.Equal("return", request.Identity.Target.Domain);
        Assert.Equal("2.0", request.Identity.Target.Version);
        Assert.Equal(42, request.Payload.GetProperty("orderId").GetInt32());
        Assert.NotEqual("source-saga", request.Identity.InvocationId);
        Assert.Null(new SagaFlow.Core.Messages.CrossDomainInstanceRelationship(request.Identity, null,
            SagaFlow.Core.Messages.ExternalProcessStatus.Requested, DateTimeOffset.UtcNow).TargetSagaInstanceId);
    }

    [Fact]
    public void RejectsMissingContextAndUndeclaredOrAmbiguousTargets()
    {
        var registry = new ProcessDefinitionRegistry();
        registry.RegisterLocalBatch([Definition(Target())]);
        var resolver = new ProcessInvocationResolver(registry);
        Assert.Throws<InvalidOperationException>(() => resolver.Create(new SagaInstance(), "return-process", 1));
        Assert.Throws<KeyNotFoundException>(() => resolver.Create(Instance("one"), "missing", 1));
        Assert.Throws<KeyNotFoundException>(() => ProcessInvocationResolver.ResolveTarget(Definition(Target(relationship: "related")), "return-process"));
        Assert.Throws<InvalidOperationException>(() => ProcessInvocationResolver.ResolveTarget(Definition(Target("return"), Target("other")), "return-process"));
        Assert.Throws<InvalidOperationException>(() => ProcessInvocationResolver.ResolveTarget(Definition(Target(version: null)), "return-process"));
        var wrongVersion = Instance("wrong");
        wrongVersion = new SagaInstance { Id = wrongVersion.Id, DefinitionDomain = "order", DefinitionName = "order-process",
            DefinitionVersion = "9.0", CorrelationId = "trace-1", Status = SagaStatus.Running, CurrentStep = "payment",
            Steps = [new() { StepName = "payment", Status = SagaStepStatus.Dispatched }] };
        Assert.Throws<InvalidOperationException>(() => resolver.Create(wrongVersion, "return-process", 1));
    }

    [Fact]
    public async Task ConcurrentRequestsKeepSourceContextIsolated()
    {
        var registry = new ProcessDefinitionRegistry();
        registry.RegisterLocalBatch([Definition(Target())]);
        var resolver = new ProcessInvocationResolver(registry);
        var requests = await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(() =>
            resolver.Create(Instance($"saga-{i}"), "return-process", new { Index = i }))));
        Assert.Equal(64, requests.Select(x => x.Identity.SourceSagaInstanceId).Distinct().Count());
        Assert.Equal(64, requests.Select(x => x.Identity.InvocationId).Distinct().Count());
    }

    [Fact]
    public void InvokesSurvivesCanonicalYamlAndRegistrySnapshot()
    {
        var codec = new DefinitionYamlCodec();
        var yaml = codec.Serialize(Definition(Target()));
        Assert.Contains("relationship: invokes", yaml);
        var parsed = codec.Parse(yaml);
        Assert.Equal("invokes", Assert.Single(parsed.BigPicture).Relationship);
        var registry = new ProcessDefinitionRegistry();
        registry.RegisterLocalBatch([parsed]);
        Assert.Equal("invokes", Assert.Single(registry.Snapshot().Single().Definition.BigPicture).Relationship);
    }

    [Fact]
    public void DifferentStepsHaveIndependentStableInvocationIds()
    {
        var definition = new SagaDefinition
        {
            Domain = "order", Name = "order-process", Version = "1.0", BigPicture = [Target()],
            Steps = [new() { Name = "payment", Command = "Pay", Transitions = [new() { Event = "success", NextStep = "next" }] },
                new() { Name = "next", Command = "Next", Transitions = [new() { Event = "success", NextStep = "completed" }] }]
        };
        var registry = new ProcessDefinitionRegistry();
        registry.RegisterLocalBatch([definition]);
        var instance = new SagaInstance
        {
            DefinitionDomain = "order", DefinitionName = "order-process", DefinitionVersion = "1.0", CorrelationId = "trace",
            Status = SagaStatus.Running, CurrentStep = "payment",
            Steps = [new() { StepName = "payment", Status = SagaStepStatus.Dispatched }, new() { StepName = "next" }]
        };
        var resolver = new ProcessInvocationResolver(registry);
        var first = resolver.Create(instance, "return-process", 1);
        Assert.Equal(first.Identity.InvocationId, resolver.Create(instance, "return-process", 1).Identity.InvocationId);
        instance.Steps[0].Status = SagaStepStatus.Completed;
        instance.Steps[1].Status = SagaStepStatus.Dispatched;
        instance.CurrentStep = "next";
        Assert.NotEqual(first.Identity.InvocationId, resolver.Create(instance, "return-process", 1).Identity.InvocationId);
    }

    [Fact]
    public void ReceiptOnlyDescribesLocalRequestRegistration()
    {
        var receipt = new SagaFlow.Core.Messages.ProcessInvocationReceipt(Guid.NewGuid().ToString("N"),
            new("return", "return-process", "2.0"), SagaFlow.Core.Messages.ExternalProcessStatus.Requested);
        Assert.Equal(SagaFlow.Core.Messages.ExternalProcessStatus.Requested, receipt.Status);
        Assert.NotNull(System.Text.Json.JsonSerializer.Serialize(receipt));
    }
}
