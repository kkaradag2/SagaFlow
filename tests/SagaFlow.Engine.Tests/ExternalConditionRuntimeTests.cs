using System.Text.Json;
using SagaFlow.Core.Definitions;
using SagaFlow.Core.State;
using SagaFlow.Core.Messages;
using SagaFlow.Engine.ProcessDefinition;

namespace SagaFlow.Engine.Tests;

public sealed partial class CrossDomainCoordinationTests
{
    [Theory]
    [InlineData("result.status")]
    [InlineData("steps.invoke.result.status")]
    [InlineData("context.missingStatus")]
    public async Task InvalidExternalConditionRetainsObservationFailsSafelyAndDoesNotRetryTransition(string path)
    {
        var s = new Setup();
        var source = await s.Start();
        var definition = s.Source.Registry.Find(new("source", "local", "1.0"))!.Definition;
        // Simulate an older/bypassed definition after normal registration; no production bypass seam.
        typeof(SagaTransitionDefinition).GetProperty(nameof(SagaTransitionDefinition.Condition))!.SetValue(
            definition.Steps[0].Transitions[0], new SagaConditionDefinition
            { Path = path, Operator = "equals", Value = JsonSerializer.SerializeToElement("Completed") });
        await s.Finish(ExternalProcessStatus.Completed);
        var failed = (await s.Source.Store.FindAsync(source.Id))!;
        Assert.Equal(SagaStatus.Failed, failed.Status);
        Assert.Null(failed.CurrentStep);
        Assert.Equal(SagaStepStatus.Failed, failed.Steps[0].Status);
        Assert.Null(failed.Steps[0].Result);
        Assert.NotNull(failed.Steps[0].CompletedAt);
        Assert.Contains("transition", failed.FailureReason);
        Assert.NotNull(failed.Steps[0].Reconciliation);
        Assert.Equal(ExternalProcessStatus.Completed, failed.Steps[0].InvocationProgress!.Status);
        Assert.Empty(s.Source.Bus.Commands);
        var reason = failed.Steps[0].Reconciliation;
        var terminal = (await s.Target.Store.ListAsync()).Single().InvocationTerminalOutcome!;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => s.SourceReceiver.ReceiveAsync(terminal)));
        var duplicate = (await s.Source.Store.FindAsync(source.Id))!;
        Assert.Equal(reason, duplicate.Steps[0].Reconciliation);
        Assert.Equal(SagaStatus.Failed, duplicate.Status);
        Assert.Empty(s.Source.Bus.Commands);
        Assert.Equal(ExternalProcessStatus.Completed, duplicate.Steps[0].InvocationProgress!.Status);
    }
}
