using System.Text.Json;
using SagaFlow.Core.State;
using SagaFlow.Engine.Coordination;

namespace SagaFlow.Engine.Tests;

public sealed class ExecutionMetadataTests
{
    [Theory]
    [InlineData("execution")]
    [InlineData("command")]
    [InlineData("command-type")]
    [InlineData("domain")]
    [InlineData("process")]
    [InlineData("version")]
    [InlineData("wire-version")]
    [InlineData("timestamp")]
    [InlineData("cause")]
    [InlineData("forward-execution")]
    [InlineData("reply")]
    [InlineData("omitted-context")]
    public async Task StoredDispatchContextRejectsConflictingOutcomeAtomically(string field)
    {
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear());
        var id = await fixture.Coordinator.StartAsync(new { sku = "a", quantity = 2 });
        var command = fixture.Bus.Commands.Single();
        var h = command.Headers;
        var altered = field switch
        {
            "execution" => h with { ExecutionId = "foreign" }, "command" => h with { CommandId = "foreign" },
            "command-type" => h with { CommandName = "foreign" }, "domain" => h with { ProcessDomain = "foreign" },
            "process" => h with { ProcessName = "foreign" }, "version" => h with { ProcessVersion = "9.0" },
            "wire-version" => h with { ProtocolVersion = 0 }, "timestamp" => h with { CreatedAt = h.CreatedAt.AddSeconds(1) },
            "cause" => h with { CausationId = "foreign" }, "forward-execution" => h with { ForwardExecutionId = "foreign" },
            "reply" => h with { ReplyDestination = "foreign" },
            _ => new() { SagaInstanceId = h.SagaInstanceId, CorrelationId = h.CorrelationId, StepId = h.StepId }
        };
        var before = await fixture.State(id);
        await Assert.ThrowsAsync<InvalidSagaOutcomeException>(() => fixture.Receiver.ReceiveAsync(new()
        { Headers = altered, IsSuccess = true, Result = JsonSerializer.SerializeToElement(new { result = 1 }) }));
        Assert.Equal(before.Revision, (await fixture.State(id)).Revision);
        Assert.Single(fixture.Bus.Commands);
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(command));
        var next = fixture.Bus.Commands.Last();
        Assert.NotEqual(h.ExecutionId, next.Headers.ExecutionId);
        Assert.Equal(next.Headers, (await fixture.State(id)).Steps[1].Execution);
    }

    [Fact]
    public async Task ExecutionContextSurvivesStoreSnapshotsAndJsonRoundTrip()
    {
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear());
        var id = await fixture.Coordinator.StartAsync(new { businessId = 42 });
        var state = await fixture.State(id);
        var rehydrated = JsonSerializer.Deserialize<SagaInstance>(JsonSerializer.Serialize(state))!;
        Assert.Equal(fixture.Bus.Commands.Single().Headers, rehydrated.Steps[0].Execution);
        await fixture.Store.SaveAsync(rehydrated);
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.Single()));
        Assert.Equal(SagaStepStatus.Completed, (await fixture.State(id)).Steps[0].Status);
    }
}
