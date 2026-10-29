using System.Text.Json;
using SagaFlow.Core.State;
using SagaFlow.Engine.ProcessDefinition;

namespace SagaFlow.Engine.Tests;

public sealed class StructuredConditionalTransitionTests
{
    [Theory]
    [InlineData("true", "equals", "true", true)]
    [InlineData("false", "equals", "true", false)]
    [InlineData("false", "notEquals", "true", true)]
    [InlineData("\"shipping\"", "equals", "\"shipping\"", true)]
    [InlineData("\"Shipping\"", "equals", "\"shipping\"", false)]
    [InlineData("\"pickup\"", "notEquals", "\"shipping\"", true)]
    [InlineData("71", "greaterThan", "70", true)]
    [InlineData("70", "greaterThan", "70", false)]
    [InlineData("1.25", "lessThan", "1.5", true)]
    [InlineData("1.50", "equals", "1.5e0", true)]
    [InlineData("-2", "lessThan", "-1", true)]
    [InlineData("0", "equals", "-0.000e20", true)]
    [InlineData("9007199254740993", "greaterThan", "9007199254740992", true)]
    [InlineData("0.123456789012345678901234567891", "notEquals", "0.123456789012345678901234567890", true)]
    [InlineData("1e999999", "greaterThan", "9e999998", true)]
    [InlineData("\"70\"", "equals", "70", false)]
    [InlineData("\"70\"", "notEquals", "70", false)]
    [InlineData("\"true\"", "equals", "true", false)]
    [InlineData("null", "equals", "null", true)]
    public async Task ContextComparisonsPreserveJsonTypesAndNumericValues(string actual, string op, string expected, bool matches)
    {
        var codec = new DefinitionYamlCodec();
        var definition = codec.Parse(BranchYaml("context.value", op, expected));
        // Canonical discovery serialization must not change strings into numbers or Booleans.
        definition = codec.Parse(codec.Serialize(definition));
        var fixture = new CoordinationTestFixture(definition);
        using var payload = JsonDocument.Parse($$"""{"value":{{actual}}}""");
        var id = await fixture.Coordinator.StartAsync(payload.RootElement);
        var outcome = fixture.Outcome(fixture.Bus.Commands.Single());
        await fixture.Receiver.ReceiveAsync(outcome);

        var state = await fixture.State(id);
        Assert.Equal(matches ? "yes" : "no", state.CurrentStep);
        Assert.Equal(actual, state.Context.GetProperty("value").GetRawText());
        Assert.Equal(outcome.Result.GetRawText(), state.Steps[0].Result!.Value.GetRawText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedContextConditionPrecedesFallbackRegardlessOfYamlOrder(bool fallbackFirst)
    {
        var fixture = new CoordinationTestFixture(new DefinitionYamlCodec().Parse(
            BranchYaml("context.customer.flags.requiresShipping", "equals", "true", fallbackFirst)));
        var id = await fixture.Coordinator.StartAsync(new { customer = new { flags = new { requiresShipping = true } } });
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.Single()));
        Assert.Equal("yes", (await fixture.State(id)).CurrentStep);
    }

    [Theory]
    [InlineData("context.missing")]
    [InlineData("context.flags.missing")]
    [InlineData("context.Value")]
    [InlineData("steps.yes.result.available")]
    public async Task MissingOrUnproducedPropertyDoesNotMatchEvenNotEquals(string path)
    {
        var fixture = new CoordinationTestFixture(new DefinitionYamlCodec().Parse(BranchYaml(path, "notEquals", "true")));
        var id = await fixture.Coordinator.StartAsync(new { value = false, flags = (object?)null });
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.Single()));
        Assert.Equal("no", (await fixture.State(id)).CurrentStep);
    }

    [Theory]
    [InlineData("result.flags.available")]
    [InlineData("steps.choose.result.flags.available")]
    public async Task CurrentOutcomeIsRecordedOnTheAtomicSnapshotBeforeTransitionEvaluation(string path)
    {
        var fixture = new CoordinationTestFixture(new DefinitionYamlCodec().Parse(BranchYaml(path, "equals", "true")));
        var id = await fixture.Coordinator.StartAsync(new { flags = new { available = false } });
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.Single(), result: new { flags = new { available = true } }));
        Assert.Equal("yes", (await fixture.State(id)).CurrentStep);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BranchOnlyDispatchesAndCompensatesStepsThatActuallyExecuted(bool shipping)
    {
        var definition = new DefinitionYamlCodec().Parse("""
            name: branching-compensation
            domain: generic
            version: '1.0'
            steps:
              - name: payment
                command: Pay
                compensation:
                  command: Refund
                transitions:
                  - on: success
                    when:
                      path: context.shipping
                      operator: equals
                      value: true
                    nextStep: shipment
                  - on: success
                    nextStep: notification
              - name: shipment
                command: Ship
                compensation:
                  command: CancelShipment
                transitions:
                  - on: success
                    nextStep: notification
              - name: notification
                command: Notify
                transitions:
                  - on: failure
                    nextStep: compensation
            """);
        var fixture = new CoordinationTestFixture(definition);
        var id = await fixture.Coordinator.StartAsync(new { shipping });
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.Last()));
        if (shipping) await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.Last()));
        Assert.Equal("Notify", fixture.Bus.Commands.Last().Name);
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.Last(), success: false));
        var plan = await fixture.State(id);
        Assert.Equal(shipping ? new[] { "shipment", "payment" } : new[] { "payment" }, plan.Compensations.Select(item => item.StepId));
        if (!shipping)
        {
            var skipped = plan.Steps.Single(step => step.StepId == "shipment");
            Assert.Equal(SagaStepStatus.Pending, skipped.Status);
            Assert.Null(skipped.ExecutionOrder);
            Assert.Null(skipped.Input);
            Assert.Null(skipped.Result);
            Assert.Null(skipped.StartedAt);
            Assert.Null(skipped.CompletedAt);
        }
        for (var i = 0; i < plan.Compensations.Count; i++)
        {
            var during = await fixture.State(id);
            Assert.Single(during.Compensations, item => item.Status == SagaCompensationStatus.Dispatched);
            await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.Last()));
        }
        Assert.Equal(SagaStatus.Compensated, (await fixture.State(id)).Status);
        Assert.Equal(shipping ? new[] { "Pay", "Ship", "Notify", "CancelShipment", "Refund" } : new[] { "Pay", "Notify", "Refund" },
            fixture.Bus.Commands.Select(command => command.Name));
    }

    [Fact]
    public async Task PreviousStepResultAndCurrentResultAreReadWithoutChangingTheirHistory()
    {
        var codec = new DefinitionYamlCodec();
        var branch = BranchYaml("steps.payment.result.risk.score", "greaterThan", "70")
            .Replace("steps:\n", """
                steps:
                  - name: payment
                    command: Pay
                    transitions:
                      - event: success
                        nextStep: choose

                """.Replace("\r", "") + "\n", StringComparison.Ordinal);
        var fixture = new CoordinationTestFixture(codec.Parse(branch));
        var id = await fixture.Coordinator.StartAsync(new { unchanged = true });
        var paymentResult = new { risk = new { score = 80 } };
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.Single(), result: paymentResult));
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.Last(), result: new { risk = new { score = 0 } }));
        var state = await fixture.State(id);
        Assert.Equal("yes", state.CurrentStep);
        Assert.Equal(80, state.Steps[0].Result!.Value.GetProperty("risk").GetProperty("score").GetInt32());
        Assert.Equal(0, state.Steps[1].Result!.Value.GetProperty("risk").GetProperty("score").GetInt32());
        Assert.True(state.Context.GetProperty("unchanged").GetBoolean());
    }

    [Fact]
    public async Task FirstMatchingConditionWinsAndOtherOutcomeRulesAreIgnored()
    {
        var yaml = BranchYaml("context.value", "greaterThan", "10")
            .Replace("    command: Choose\n    transitions:\n", """
                    command: Choose
                    transitions:
                      - on: failure
                        when:
                          path: context.value
                          operator: greaterThan
                          value: 0
                        nextStep: no

                """.Replace("\r", "") + "\n", StringComparison.Ordinal)
            .Replace("      - event: success\n        nextStep: no", """
                      - on: success
                        when:
                          path: context.value
                          operator: greaterThan
                          value: 5
                        nextStep: no
                      - event: success
                        nextStep: compensation
                """.Replace("\r", ""), StringComparison.Ordinal);
        var definition = new DefinitionYamlCodec().Parse(yaml);
        Assert.Equal(2, definition.Steps[0].Transitions.Count(rule => rule.Event == "success" && rule.Condition is not null));
        Assert.Equal("failure", definition.Steps[0].Transitions[0].Event);
        var fixture = new CoordinationTestFixture(definition);
        var id = await fixture.Coordinator.StartAsync(new { value = 20 });
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.Single()));
        Assert.Equal("yes", (await fixture.State(id)).CurrentStep);
    }

    [Fact]
    public async Task NoMatchAndNoFallbackRejectsOutcomeAtomicallyWithAnExplicitError()
    {
        var yaml = BranchYaml("result.available", "equals", "true")
            .Replace("      - event: success\n        nextStep: no\n", "", StringComparison.Ordinal)
            .Replace("  - name: no\n    command: No\n    transitions:\n      - event: success\n        nextStep: completed", "", StringComparison.Ordinal);
        var fixture = new CoordinationTestFixture(new DefinitionYamlCodec().Parse(yaml));
        var id = await fixture.Coordinator.StartAsync("payload");
        var before = await fixture.State(id);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Receiver.ReceiveAsync(
            fixture.Outcome(fixture.Bus.Commands.Single(), result: new { available = false })));
        Assert.Contains("no matching 'success' transition or fallback", error.Message);
        var after = await fixture.State(id);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(SagaStepStatus.Dispatched, after.Steps[0].Status);
        Assert.Null(after.Steps[0].Result);
        Assert.Single(fixture.Bus.Commands);
    }

    [Theory]
    [InlineData("operator: equals", "operator: execute", "operator")]
    [InlineData("path: context.value", "path: ''", "path")]
    [InlineData("path: context.value", "path: context..value", "path")]
    [InlineData("path: context.value", "path: context.items[0]", "path")]
    [InlineData("path: context.value", "path: context.value()", "path")]
    [InlineData("path: context.value", "path: data.value", "path")]
    [InlineData("path: context.value", "path: steps.missing.result.value", "path")]
    [InlineData("operator: equals", "operator: greaterThan", "numeric")]
    [InlineData("value: true", "value: [true]", "invalid YAML")]
    [InlineData("value: true", "", "value")]
    [InlineData("nextStep: yes", "nextStep: missing", "unknown nextStep")]
    [InlineData("operator: equals", "operator: equals\n          operator: notEquals", "invalid YAML")]
    [InlineData("on: success", "on: success\n        event: success", "invalid YAML")]
    public void InvalidConditionsAreRejectedDuringDefinitionLoading(string oldText, string newText, string expected)
    {
        var yaml = BranchYaml("context.value", "equals", "true").Replace(oldText, newText, StringComparison.Ordinal);
        var error = Assert.Throws<DefinitionValidationException>(() => new DefinitionYamlCodec().Parse(yaml));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void DuplicateFallbacksRemainInvalidWithStructuredConditions()
    {
        var yaml = BranchYaml("context.value", "equals", "true").Replace("        nextStep: no", "        nextStep: no\n      - on: success\n        nextStep: compensation", StringComparison.Ordinal);
        Assert.Contains("multiple fallbacks", Assert.Throws<DefinitionValidationException>(() => new DefinitionYamlCodec().Parse(yaml)).Message);
    }

    private static string BranchYaml(string path, string op, string value, bool fallbackFirst = false)
    {
        var conditional = $"      - on: success\n        when:\n          path: {path}\n          operator: {op}\n          value: {value}\n        nextStep: yes\n";
        var fallback = "      - event: success\n        nextStep: no\n";
        return "name: branch\ndomain: generic\nversion: '1.0'\nsteps:\n  - name: choose\n    command: Choose\n    transitions:\n"
            + (fallbackFirst ? fallback + conditional : conditional + fallback)
            + "  - name: yes\n    command: Yes\n    transitions:\n      - event: success\n        nextStep: completed\n"
            + "  - name: no\n    command: No\n    transitions:\n      - event: success\n        nextStep: completed";
    }
}
