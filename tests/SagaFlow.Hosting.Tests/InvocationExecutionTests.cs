using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Definitions;
using SagaFlow.Core.Messages;
using SagaFlow.Engine.Coordination;
using SagaFlow.Engine.ProcessDefinition;
using SagaFlow.Hosting.DependencyInjection;

namespace SagaFlow.Hosting.Tests;
public sealed class InvocationExecutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EngineCreatesIsolatedScopesAndStableLocalRegistrations(bool fail)
    {
        var services = new ServiceCollection();
        services.AddSagaFlow();
        services.AddSingleton<ISagaMessageBus, Bus>();
        services.AddScoped<ILocalSagaStepHandler, Handler>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        provider.GetRequiredService<ProcessDefinitionRegistry>().RegisterLocalBatch([new SagaDefinition
        {
            Domain = "source", Name = "source-process", Version = "1.0",
            Steps = [new() { Name = "invoke", Command = "Invoke", Transitions = [new() { Event = "success", NextStep = "completed" }] }],
            BigPicture = [new() { Domain = "target", Process = "target-process", Version = "1.0", Endpoint = "https://discovery", Relationship = "invokes" }]
        }]);
        var tasks = Enumerable.Range(0, 8).Select(async i =>
        {
            await using var scope = provider.CreateAsyncScope();
            var flow = scope.ServiceProvider.GetRequiredService<ISagaFlow>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => flow.InvokeAsync("target-process", i));
            if (fail)
            {
                await Assert.ThrowsAsync<SagaDispatchException>(() => flow.StartAsync("fail"));
                return Guid.Empty;
            }
            return await flow.StartAsync(i);
        });
        var ids = await Task.WhenAll(tasks);
        var store = provider.GetRequiredService<ISagaStateStore>();
        var states = await store.ListAsync();
        Assert.Equal(8, states.Count);
        Assert.Equal(8, states.Select(s => s.Steps[0].Invocation!.Identity.InvocationId).Distinct().Count());
        foreach (var state in states)
            Assert.Equal(state.Id, state.Steps[0].Invocation!.Identity.SourceSagaInstanceId);
        foreach (var captured in Handler.Captured)
            await Assert.ThrowsAsync<InvalidOperationException>(() => captured.InvokeAsync("target-process", 1));
    }
    private sealed class Handler(ISagaFlow flow) : ILocalSagaStepHandler
    {
        public static ConcurrentBag<ISagaFlow> Captured { get; } = [];
        public async Task ExecuteAsync(SagaCommand command, CancellationToken cancellationToken)
        {
            var first = await flow.InvokeAsync("target-process", new { Value = 1 }, cancellationToken);
            await Task.Yield();
            var second = await flow.InvokeAsync("target-process", new { Value = 1 }, cancellationToken);
            Assert.Equal(first.InvocationId, second.InvocationId);
            Assert.Equal(ExternalProcessStatus.Prepared, first.Status);
            Captured.Add(flow);
            if (command.Payload.ValueKind == System.Text.Json.JsonValueKind.String) throw new OperationCanceledException();
        }
    }
    private sealed class Bus : ISagaMessageBus
    {
        public ValueTask PublishAsync(SagaCommand command, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
