using Microsoft.Extensions.Time.Testing;
using SagaFlow.Core.Definitions;
using SagaFlow.Engine.Discovery;
using SagaFlow.Engine.ProcessDefinition;

namespace SagaFlow.Engine.Tests;

public sealed class DiscoveryDeadlineTests
{
    [Fact]
    public async Task OverallBudgetPreservesPartialGraphAndStopsIssuingRequests()
    {
        var time = new FakeTimeProvider();
        var called = new List<string>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client(async (source, token) =>
        {
            called.Add(source.Process);
            if (source.Process == "first") return Envelope("first");
            started.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            throw new InvalidOperationException();
        });
        var root = Definition("root", [Source("first"), Source("slow"), Source("never")]);
        var registry = new ProcessDefinitionRegistry();
        registry.RegisterLocalBatch([root]);
        var discovery = Discovery(client, overall: TimeSpan.FromMilliseconds(300), timeProvider: time);
        var pending = discovery.DiscoverAsync(registry.Find(ProcessDefinitionId.From(root))!.Definition);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromMilliseconds(299));
        Assert.False(pending.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));
        var graph = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { "first", "slow" }, called);
        Assert.Equal(new[] { "root", "first" }, graph.Nodes.Select(n => n.Id.Name));
        Assert.Equal(DiscoveryFailureKind.OverallTimeout, graph.Failures.Single().Kind);
        Assert.DoesNotContain(graph.Edges, edge => edge.TargetProcess == "never");
        Assert.Single(registry.Snapshot());
        Assert.Equal(DefinitionOrigin.Local, registry.Snapshot().Single().Origin);
    }

    [Fact]
    public async Task PerRequestTimeoutAllowsNextRequestWithinOverallBudget()
    {
        var time = new FakeTimeProvider();
        var called = new List<string>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client(async (source, token) =>
        {
            called.Add(source.Process);
            if (source.Process == "slow")
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return Envelope(source.Process);
        });
        var pending = Discovery(client, request: TimeSpan.FromMilliseconds(80), overall: TimeSpan.FromSeconds(3), timeProvider: time)
            .DiscoverAsync(Definition("root", [Source("slow"), Source("second")]));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromMilliseconds(80));
        var graph = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "slow", "second" }, called);
        Assert.Equal(DiscoveryFailureKind.Timeout, graph.Failures.Single().Kind);
        Assert.Contains(graph.Nodes, node => node.Id.Name == "second");
    }

    [Fact]
    public async Task CallerCancellationDuringActiveRequestPropagatesAndStopsTraversal()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var called = new List<string>();
        var client = new Client(async (source, token) =>
        {
            called.Add(source.Process);
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Envelope(source.Process);
        });
        using var cancellation = new CancellationTokenSource();
        var pending = Discovery(client).DiscoverAsync(Definition("root", [Source("slow"), Source("never")]), cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(new[] { "slow" }, called);
    }

    [Fact]
    public async Task OverallBudgetAlsoStopsNestedTraversalAndItsParentSiblings()
    {
        var time = new FakeTimeProvider();
        var called = new List<string>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client(async (source, token) =>
        {
            called.Add(source.Process);
            if (source.Process == "branch") return Envelope("branch", [Source("slow")]);
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Envelope(source.Process);
        });
        var pending = Discovery(client, overall: TimeSpan.FromMilliseconds(250), timeProvider: time)
            .DiscoverAsync(Definition("root", [Source("branch"), Source("never")]));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromMilliseconds(250));
        var graph = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "branch", "slow" }, called);
        Assert.Equal(2, graph.Nodes.Count);
        Assert.Equal(DiscoveryFailureKind.OverallTimeout, graph.Failures.Single().Kind);
        Assert.Equal("branch", graph.Failures.Single().Source.Name);
    }

    [Fact]
    public void InvalidOverallBudgetsAreRejected()
    {
        var client = new Client((source, _) => Task.FromResult(Envelope(source.Process)));
        Assert.Throws<ArgumentException>(() => Discovery(client, overall: TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => Discovery(client, overall: TimeSpan.FromMinutes(6)));
    }

    private static DefinitionDiscovery Discovery(IRemoteDefinitionClient client, TimeSpan? request = null, TimeSpan? overall = null, TimeProvider? timeProvider = null)
    {
        var options = new DefinitionDiscoveryOptions { RequestTimeout = request ?? TimeSpan.FromSeconds(3), OverallTimeout = overall ?? TimeSpan.FromSeconds(5) };
        options.AllowedEndpoints.Add("https://remote");
        return new(client, options, timeProvider ?? TimeProvider.System);
    }
    private static BigPictureSourceDefinition Source(string name) => new() { Domain = name, Process = name, Endpoint = "https://remote" };
    private static RemoteDefinitionEnvelope Envelope(string name, BigPictureSourceDefinition[]? sources = null) =>
        new(1, name, name, "1.0", new DefinitionYamlCodec().Serialize(Definition(name, sources)));
    private static SagaDefinition Definition(string name, BigPictureSourceDefinition[]? sources = null) => new()
    {
        Name = name, Domain = name, Version = "1.0", BigPicture = sources ?? [],
        Steps = [new() { Name = "work", Command = "Work", Transitions = [new() { Event = "success", NextStep = "completed" }] }]
    };
    private sealed class Client(Func<BigPictureSourceDefinition, CancellationToken, Task<RemoteDefinitionEnvelope>> get) : IRemoteDefinitionClient
    {
        public Task<RemoteDefinitionEnvelope> GetAsync(Uri endpoint, BigPictureSourceDefinition source, TimeSpan timeout, CancellationToken cancellationToken) => get(source, cancellationToken);
    }
}
