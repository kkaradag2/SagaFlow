using SagaFlow.Core.Definitions;
using SagaFlow.Core.State;
using SagaFlow.Engine.Coordination;
using SagaFlow.Engine.Timeouts;

namespace SagaFlow.Engine.Tests;

public sealed class SagaTimeoutDetectorTests
{
    [Fact]
    public async Task DetectsOnlyAtTheConfiguredDeadlineAndPersistsTimeoutFacts()
    {
        var fixture = new CoordinationTestFixture(Definition());
        var id = await fixture.Coordinator.StartAsync(new { orderId = 7 });
        var dispatched = await fixture.State(id);
        var startedAt = dispatched.Steps[0].StartedAt!.Value;
        var time = new ManualTimeProvider(startedAt);
        var detector = new SagaTimeoutDetector(fixture.Store, time);

        time.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1));
        Assert.Equal(0, await detector.DetectAsync());
        Assert.Equal(SagaStepStatus.Dispatched, (await fixture.State(id)).Steps[0].Status);

        time.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(1, await detector.DetectAsync());
        var timedOut = await fixture.State(id);
        Assert.Equal(SagaStatus.TimedOut, timedOut.Status);
        Assert.Equal("work", timedOut.CurrentStep);
        Assert.Equal(SagaStepStatus.TimedOut, timedOut.Steps[0].Status);
        Assert.Equal(TimeSpan.FromSeconds(5), timedOut.Steps[0].TimeoutLimit);
        Assert.Equal(time.GetUtcNow(), timedOut.Steps[0].TimedOutAt);
        Assert.Equal(startedAt, timedOut.Steps[0].StartedAt);
        Assert.Null(timedOut.Steps[0].CompletedAt);
        Assert.Equal(TimeSpan.FromSeconds(5), Assert.Single(fixture.Bus.Commands).TimeToLive);
        Assert.Empty(timedOut.Compensations);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OutcomeBeforeDeadlinePreventsTimeout(bool success)
    {
        var fixture = new CoordinationTestFixture(Definition());
        var id = await fixture.Coordinator.StartAsync("payload");
        var state = await fixture.State(id);
        var time = new ManualTimeProvider(state.Steps[0].StartedAt!.Value);
        var detector = new SagaTimeoutDetector(fixture.Store, time);

        time.Advance(TimeSpan.FromSeconds(4));
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.Single(), success));
        time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(0, await detector.DetectAsync());
        var after = await fixture.State(id);
        Assert.Equal(success ? SagaStepStatus.Completed : SagaStepStatus.Failed, after.Steps[0].Status);
        Assert.Null(after.Steps[0].TimedOutAt);
    }

    [Fact]
    public async Task TimeoutIsAppliedOnceAndNeverDispatchesOrPlansCompensation()
    {
        var fixture = new CoordinationTestFixture(Definition());
        var id = await fixture.Coordinator.StartAsync("payload");
        var state = await fixture.State(id);
        var time = new ManualTimeProvider(state.Steps[0].StartedAt!.Value.AddSeconds(5));
        var detector = new SagaTimeoutDetector(fixture.Store, time);

        Assert.Equal(1, await detector.DetectAsync());
        var revision = (await fixture.State(id)).Revision;
        Assert.Equal(0, await detector.DetectAsync());
        var after = await fixture.State(id);

        Assert.Equal(revision, after.Revision);
        Assert.Single(fixture.Bus.Commands);
        Assert.Empty(after.Compensations);
        Assert.Equal(SagaStepStatus.Pending, after.Steps[1].Status);
    }

    [Fact]
    public async Task TimeoutAndOutcomeRaceHasExactlyOneWinner()
    {
        var fixture = new CoordinationTestFixture(Definition());
        var id = await fixture.Coordinator.StartAsync("payload");
        var state = await fixture.State(id);
        var command = fixture.Bus.Commands.Single();
        var detector = new SagaTimeoutDetector(fixture.Store,
            new ManualTimeProvider(state.Steps[0].StartedAt!.Value.AddSeconds(5)));

        var detection = Task.Run(() => detector.DetectAsync());
        var outcome = Task.Run(async () =>
        {
            try { await fixture.Receiver.ReceiveAsync(fixture.Outcome(command)); return true; }
            catch (LateSagaOutcomeException) { return false; }
        });
        await Task.WhenAll(detection, outcome);
        var detected = await detection;
        var outcomeAccepted = await outcome;

        var after = await fixture.State(id);
        if (after.Status == SagaStatus.TimedOut)
        {
            Assert.False(outcomeAccepted);
            Assert.Equal(1, detected);
            Assert.Single(fixture.Bus.Commands);
        }
        else
        {
            Assert.True(outcomeAccepted);
            Assert.Equal(0, detected);
            Assert.Equal(SagaStepStatus.Completed, after.Steps[0].Status);
            Assert.Equal(2, fixture.Bus.Commands.Count);
        }
    }

    [Fact]
    public async Task DifferentInstancesTimeoutIndependentlyWithoutMixingState()
    {
        var fixture = new CoordinationTestFixture(Definition());
        var firstId = await fixture.Coordinator.StartAsync(new { value = 1 });
        var secondId = await fixture.Coordinator.StartAsync(new { value = 2 });
        var second = await fixture.State(secondId);
        var time = new ManualTimeProvider(second.Steps[0].StartedAt!.Value.AddSeconds(5));

        Assert.Equal(2, await new SagaTimeoutDetector(fixture.Store, time).DetectAsync());
        var first = await fixture.State(firstId);
        second = await fixture.State(secondId);
        Assert.Equal(1, first.Context.GetProperty("value").GetInt32());
        Assert.Equal(2, second.Context.GetProperty("value").GetInt32());
        Assert.Equal(SagaStepStatus.TimedOut, first.Steps[0].Status);
        Assert.Equal(SagaStepStatus.TimedOut, second.Steps[0].Status);
        Assert.Equal(time.GetUtcNow(), first.Steps[0].TimedOutAt);
        Assert.Equal(time.GetUtcNow(), second.Steps[0].TimedOutAt);
    }

    [Fact]
    public async Task StepWithoutTimeoutLimitIsNeverAffected()
    {
        var fixture = new CoordinationTestFixture(Definition(timeout: null));
        var id = await fixture.Coordinator.StartAsync("payload");
        var state = await fixture.State(id);
        var time = new ManualTimeProvider(state.Steps[0].StartedAt!.Value.AddDays(30));

        Assert.Equal(0, await new SagaTimeoutDetector(fixture.Store, time).DetectAsync());
        Assert.Equal(SagaStepStatus.Dispatched, (await fixture.State(id)).Steps[0].Status);
        Assert.Null(Assert.Single(fixture.Bus.Commands).TimeToLive);
    }

    [Fact]
    public async Task OutcomeAfterTimeoutIsLateNotDuplicateAndCannotMutateState()
    {
        var fixture = new CoordinationTestFixture(Definition());
        var id = await fixture.Coordinator.StartAsync("payload");
        var command = fixture.Bus.Commands.Single();
        var state = await fixture.State(id);
        var detector = new SagaTimeoutDetector(fixture.Store,
            new ManualTimeProvider(state.Steps[0].StartedAt!.Value.AddSeconds(5)));
        await detector.DetectAsync();
        var before = await fixture.State(id);

        await Assert.ThrowsAsync<LateSagaOutcomeException>(() =>
            fixture.Receiver.ReceiveAsync(fixture.Outcome(command)));

        var after = await fixture.State(id);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(SagaStatus.TimedOut, after.Status);
        Assert.Equal(SagaStepStatus.TimedOut, after.Steps[0].Status);
        Assert.Null(after.Steps[0].Result);
        Assert.Single(fixture.Bus.Commands);
    }

    private static SagaDefinition Definition(string? timeout = "5s") => new()
    {
        Domain = "order", Name = "timeout-process", Version = "1.0",
        Steps =
        [
            new()
            {
                Name = "work", Command = "DoWork", TimeoutLimit = timeout,
                Compensation = new() { Command = "UndoWork" },
                Transitions =
                [
                    new() { Event = "success", NextStep = "next" },
                    new() { Event = "failure", NextStep = "compensation" },
                    new() { Event = "timeout", NextStep = "compensation" }
                ]
            },
            new()
            {
                Name = "next", Command = "DoNext",
                Transitions =
                [
                    new() { Event = "success", NextStep = "completed" },
                    new() { Event = "failure", NextStep = "compensation" }
                ]
            }
        ]
    };

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _utcNow = now;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
