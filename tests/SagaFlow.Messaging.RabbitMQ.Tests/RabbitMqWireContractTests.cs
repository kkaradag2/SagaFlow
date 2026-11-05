using Microsoft.Extensions.DependencyInjection;
using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using SagaFlow.Core.State;

namespace SagaFlow.Messaging.RabbitMQ.Tests;

public sealed partial class RabbitMqTransportIntegrationTests
{
    private const string WireDefinition = """
        name: wire-process
        domain: test
        version: '1.0'
        steps:
          - name: work
            command: ReserveSomething
            compensation:
              command: ReleaseSomething
            transitions:
              - event: success
                nextStep: nextWork
              - event: failure
                nextStep: compensation
          - name: nextWork
            command: SendSomething
            transitions:
              - event: success
                nextStep: completed
              - event: failure
                nextStep: compensation
        """;

    // A participant written in any language can implement this echo, with no engine models.
    private static BasicProperties EchoOutcome(BasicGetResult command, bool success = true)
    {
        var headers = command.BasicProperties.Headers!.ToDictionary(pair => pair.Key, pair => pair.Value);
        headers["sf-causation-id"] = command.BasicProperties.MessageId!;
        headers["sf-outcome"] = success ? "success" : "failure";
        return new()
        {
            Headers = headers, ContentType = "application/json", Type = "participant.business-result",
            MessageId = "participant-" + Guid.NewGuid().ToString("N"), CorrelationId = command.BasicProperties.CorrelationId
        };
    }

    private static void AssertBusinessBody(BasicGetResult delivery, JsonElement expected)
    {
        using var body = JsonDocument.Parse(delivery.Body);
        Assert.True(JsonElement.DeepEquals(expected, body.RootElement));
    }

    [RabbitMqFact]
    public async Task TwoBusinessSchemasAdvanceWithNewExecutionAndDuplicateCannotDispatchAgain()
    {
        await using var runtime = await StartExternalAsync(definitionYaml: WireDefinition);
        foreach (var json in new[] { "{\"invoice\":{\"number\":17},\"amount\":25.4}",
            "{\"products\":[\"a\",\"b\"],\"destination\":\"Ankara\",\"headers\":{\"customer\":true}}" })
        {
            using var business = JsonDocument.Parse(json);
            var id = await StartSagaAsync(runtime.Host, business.RootElement.Clone());
            var first = await GetMessageAsync("work");
            AssertBusinessBody(first, business.RootElement);
            var firstCommand = RabbitMqMessageSerializer.DeserializeCommand(first);
            Assert.Equal(id.ToString("N"), firstCommand.Headers.SagaInstanceId);
            Assert.Equal(firstCommand.Headers.CommandId, first.BasicProperties.MessageId);
            Assert.Equal(runtime.Topology.OutcomeQueue, first.BasicProperties.ReplyTo);
            var reply = EchoOutcome(first);
            Assert.NotEqual(first.BasicProperties.MessageId, reply.MessageId);
            Assert.Equal(first.BasicProperties.MessageId, reply.Headers!["sf-causation-id"]);
            const string result = "{\"reservation\":\"R-42\",\"linesAccepted\":2}";
            // Inspect an actual broker outcome before delivering identical bytes/properties to SagaFlow.
            await using var tap = await broker.Connection.CreateChannelAsync();
            var audit = (await tap.QueueDeclareAsync()).QueueName;
            await PublishRawAsync(audit, Encoding.UTF8.GetBytes(result), reply);
            var observed = await GetMessageAsync(audit);
            using var expected = JsonDocument.Parse(result);
            AssertBusinessBody(observed, expected.RootElement);
            Assert.Equal("success", Encoding.UTF8.GetString((byte[])observed.BasicProperties.Headers!["sf-outcome"]!));
            await PublishRawAsync(first.BasicProperties.ReplyTo!, observed.Body.ToArray(), reply);
            var second = await GetMessageAsync("nextWork");
            AssertBusinessBody(second, business.RootElement);
            var secondCommand = RabbitMqMessageSerializer.DeserializeCommand(second);
            Assert.Equal(firstCommand.Headers.SagaInstanceId, secondCommand.Headers.SagaInstanceId);
            Assert.NotEqual(firstCommand.Headers.ExecutionId, secondCommand.Headers.ExecutionId);
            Assert.NotEqual(firstCommand.Headers.CommandId, secondCommand.Headers.CommandId);
            Assert.Equal(reply.MessageId, secondCommand.Headers.CausationId);
            var before = (await FindSagaAsync(runtime.Host, id))!;
            var transport = runtime.Host.Services.GetRequiredService<RabbitMqTransport>();
            var acknowledged = transport.DuplicateAcknowledgementCount;
            await PublishRawAsync(first.BasicProperties.ReplyTo!, observed.Body.ToArray(), reply);
            await WaitForConsumerAsync(runtime.Topology.OutcomeQueue);
            await WaitUntilAsync(() => Task.FromResult(transport.DuplicateAcknowledgementCount == acknowledged + 1));
            Assert.Equal(before.Revision, (await FindSagaAsync(runtime.Host, id))!.Revision);
            Assert.Equal(0u, await MessageCountAsync("nextWork"));
            // A previous execution cannot be relabelled as the active step.
            var forged = EchoOutcome(first);
            forged.Headers!["sf-ex-step-id"] = "nextWork";
            await PublishRawAsync(first.BasicProperties.ReplyTo!, "{}"u8.ToArray(), forged);
            await WaitForConsumerAsync(runtime.Topology.OutcomeQueue);
            Assert.Equal(before.Revision, (await FindSagaAsync(runtime.Host, id))!.Revision);
            await PublishRawAsync(second.BasicProperties.ReplyTo!, "{\"deliveryCode\":204}"u8.ToArray(), EchoOutcome(second));
            await WaitUntilAsync(async () => (await FindSagaAsync(runtime.Host, id))?.Status == SagaStatus.Completed);
        }
    }

    [RabbitMqFact]
    public async Task MissingConflictingAndLegacyMetadataNeverMutatesState()
    {
        await using var runtime = await StartExternalAsync();
        var id = await StartSagaAsync(runtime.Host, new { product = "sku", quantity = 3 });
        var command = await GetMessageAsync(runtime.Topology.CommandQueue);
        var before = (await FindSagaAsync(runtime.Host, id))!;
        foreach (var key in command.BasicProperties.Headers!.Keys.Where(key => key.StartsWith("sf-", StringComparison.Ordinal)).Append("sf-causation-id").Append("sf-outcome"))
        {
            var missing = EchoOutcome(command);
            missing.Headers!.Remove(key);
            await PublishRawAsync(command.BasicProperties.ReplyTo!, "{}"u8.ToArray(), missing);
            await WaitForConsumerAsync(runtime.Topology.OutcomeQueue);
            Assert.Equal(before.Revision, (await FindSagaAsync(runtime.Host, id))!.Revision);
            var wrong = EchoOutcome(command);
            wrong.Headers![key] = "unexpected-value";
            await PublishRawAsync(command.BasicProperties.ReplyTo!, "{}"u8.ToArray(), wrong);
            await WaitForConsumerAsync(runtime.Topology.OutcomeQueue);
            Assert.Equal(before.Revision, (await FindSagaAsync(runtime.Host, id))!.Revision);
        }
        foreach (var change in new Action<BasicProperties>[] { p => p.CorrelationId = "foreign", p => p.MessageId = null,
            p => p.Type = null, p => p.ContentType = "text/plain", p => p.Headers!["sf-ex-step-id"] = 123,
            p => p.Headers!["sf-ex-unknown"] = "foreign", p => p.Headers!["sf-ex-step-id"] = new byte[] { 0xff } })
        {
            var wrong = EchoOutcome(command); change(wrong);
            await PublishRawAsync(command.BasicProperties.ReplyTo!, "{}"u8.ToArray(), wrong);
            await WaitForConsumerAsync(runtime.Topology.OutcomeQueue);
            Assert.Equal(before.Revision, (await FindSagaAsync(runtime.Host, id))!.Revision);
        }
        await PublishRawAsync(command.BasicProperties.ReplyTo!, "{\"name\":\"WorkCompleted\",\"isSuccess\":true,\"headers\":{}}"u8.ToArray());
        await WaitForConsumerAsync(runtime.Topology.OutcomeQueue);
        Assert.Equal(before.Revision, (await FindSagaAsync(runtime.Host, id))!.Revision);
        await PublishRawAsync(command.BasicProperties.ReplyTo!, "[17,\"business-result\"]"u8.ToArray(), EchoOutcome(command));
        await WaitUntilAsync(async () => (await FindSagaAsync(runtime.Host, id))?.Status == SagaStatus.Completed);
    }

    [RabbitMqFact]
    public async Task FailureAndCompensationUseBusinessBodiesAndDistinctExecutionContexts()
    {
        await using var runtime = await StartExternalAsync(definitionYaml: WireDefinition);
        var business = JsonSerializer.SerializeToElement(new { invoice = "INV-3", amount = 10 });
        var id = await StartSagaAsync(runtime.Host, business);
        var forward = await GetMessageAsync("work");
        await PublishRawAsync(forward.BasicProperties.ReplyTo!, "{\"reserved\":true}"u8.ToArray(), EchoOutcome(forward));
        var next = await GetMessageAsync("nextWork");
        var failure = EchoOutcome(next, success: false);
        // No success/failure field in the business result.
        await PublishRawAsync(next.BasicProperties.ReplyTo!, "{\"code\":\"DESTINATION_UNAVAILABLE\"}"u8.ToArray(), failure);
        var undo = await GetMessageAsync("work");
        AssertBusinessBody(undo, business);
        var undoCommand = RabbitMqMessageSerializer.DeserializeCommand(undo);
        var forwardCommand = RabbitMqMessageSerializer.DeserializeCommand(forward);
        Assert.True(undoCommand.Headers.IsCompensation);
        Assert.Equal("ReleaseSomething", undo.BasicProperties.Type);
        Assert.Equal(forwardCommand.Headers.ExecutionId, undoCommand.Headers.ForwardExecutionId);
        Assert.NotEqual(forwardCommand.Headers.ExecutionId, undoCommand.Headers.ExecutionId);
        Assert.Equal(failure.MessageId, undoCommand.Headers.CausationId);
        await PublishRawAsync(undo.BasicProperties.ReplyTo!, "{\"releaseReference\":\"U-9\"}"u8.ToArray(), EchoOutcome(undo));
        await WaitUntilAsync(async () => (await FindSagaAsync(runtime.Host, id))?.Status == SagaStatus.Compensated);
        var state = (await FindSagaAsync(runtime.Host, id))!;
        Assert.Equal(SagaCompensationStatus.Completed, state.Compensations.Single().Status);
        Assert.Equal("U-9", state.Compensations.Single().Result!.Value.GetProperty("releaseReference").GetString());
        await PublishRawAsync(undo.BasicProperties.ReplyTo!, "{}"u8.ToArray(), EchoOutcome(undo));
        await WaitForConsumerAsync(runtime.Topology.OutcomeQueue);
        Assert.Equal(state.Revision, (await FindSagaAsync(runtime.Host, id))!.Revision);
        Assert.Equal(0u, await MessageCountAsync("work"));
    }
}
