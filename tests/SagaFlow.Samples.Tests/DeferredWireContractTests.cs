using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using RabbitMQ.Client;
using SagaFlow.ManagerApproval;

namespace SagaFlow.Samples.Tests;

[Collection("sample-rabbitmq-e2e")]
public sealed partial class DeferredWireContractTests
{
    [SampleRabbitMqFact]
    public async Task ManagerAndWarehouseKeepTwoOpaqueContextsAndReplyBusinessOnlyToOriginalDestinations()
    {
        var vhost = SampleRabbitMqTestIsolation.RequireVirtualHost();
        var factory = new ConnectionFactory
        {
            HostName = Environment.GetEnvironmentVariable("SAGAFLOW_RABBITMQ_HOST") ?? "localhost",
            UserName = Environment.GetEnvironmentVariable("SAGAFLOW_RABBITMQ_USERNAME")!,
            Password = Environment.GetEnvironmentVariable("SAGAFLOW_RABBITMQ_PASSWORD")!, VirtualHost = vhost
        };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true));
        foreach (var queue in new[] { "managerApproval", "warehouse" })
            await channel.QueueDeclareAsync(queue, true, false, false);
        await using var manager = new WebApplicationFactory<ManagerApprovalProgram>();
        await using var warehouse = new WebApplicationFactory<WarehouseProgram>();
        using var managerApi = manager.CreateClient();
        using var warehouseApi = warehouse.CreateClient();
        await Wait(async () => await channel.ConsumerCountAsync("managerApproval") > 0 && await channel.ConsumerCountAsync("warehouse") > 0);
        var commands = new List<(HttpClient Api, Guid Id, string Path, string Field, BasicProperties Properties, string Reply)>();
        foreach (var isManager in new[] { true, false })
        foreach (var index in new[] { 0, 1 })
        {
            var id = Guid.NewGuid();
            var reply = (await channel.QueueDeclareAsync()).QueueName;
            var properties = CommandProperties(isManager ? "RequestManagerApproval" : "InspectReturn",
                isManager ? "managerApproval" : "warehouse", reply);
            var body = isManager ? JsonSerializer.SerializeToUtf8Bytes(new { id, totalQuantity = 1001 + index })
                : JsonSerializer.SerializeToUtf8Bytes(new { returnId = id, orderId = Guid.NewGuid() });
            await channel.BasicPublishAsync("sagaflow.commands", isManager ? "managerApproval" : "warehouse", true, properties, body);
            commands.Add((isManager ? managerApi : warehouseApi, id, isManager ? "approvals" : "inspections",
                isManager ? "approved" : "accepted", properties, reply));
        }
        foreach (var command in commands)
        {
            await Wait(async () => (await command.Api.GetAsync($"/{command.Path}/{command.Id}")).StatusCode == HttpStatusCode.OK);
            using var businessRecord = JsonDocument.Parse(await command.Api.GetStringAsync($"/{command.Path}/{command.Id}"));
            foreach (var field in new[] { "sagaInstanceId", "correlationId", "stepId", "replyTo", "transportContext", "transportHandle" })
                Assert.False(businessRecord.RootElement.TryGetProperty(field, out _));
        }
        // All four have been accepted and ACKed; no outcome exists before the later decisions.
        foreach (var command in commands) Assert.Null(await channel.BasicGetAsync(command.Reply, true));
        foreach (var command in commands.AsEnumerable().Reverse())
        {
            var decision = command.Path == "approvals" ? "approve" : "accept";
            Assert.Equal(HttpStatusCode.OK, (await command.Api.PostAsJsonAsync($"/{command.Path}/{command.Id}/decision", new { decision })).StatusCode);
            BasicGetResult? delivery = null;
            await Wait(async () => (delivery = await channel.BasicGetAsync(command.Reply, true)) is not null);
            using var result = JsonDocument.Parse(delivery!.Body);
            Assert.Single(result.RootElement.EnumerateObject());
            Assert.True(result.RootElement.GetProperty(command.Field).GetBoolean());
            Assert.NotEqual(command.Properties.MessageId, delivery.BasicProperties.MessageId);
            Assert.Equal(command.Properties.CorrelationId, delivery.BasicProperties.CorrelationId);
            foreach (var (key, value) in command.Properties.Headers!.Where(pair => pair.Key == "sf-version" || pair.Key.StartsWith("sf-ex-", StringComparison.Ordinal)))
            {
                var observed = delivery.BasicProperties.Headers![key];
                Assert.Equal(value!.ToString(), observed is byte[] bytes ? Encoding.UTF8.GetString(bytes) : observed?.ToString());
            }
            Assert.Equal(command.Properties.MessageId,
                Encoding.UTF8.GetString((byte[])delivery.BasicProperties.Headers!["sf-causation-id"]!));
            Assert.Equal("success", Encoding.UTF8.GetString((byte[])delivery.BasicProperties.Headers!["sf-outcome"]!));
        }
        foreach (var command in commands) Assert.Null(await channel.BasicGetAsync(command.Reply, true));
    }

    private static BasicProperties CommandProperties(string type, string step, string reply)
    {
        var commandId = Guid.NewGuid().ToString("N");
        var correlation = Guid.NewGuid().ToString("N");
        return new()
        {
            Type = type, MessageId = commandId, CorrelationId = correlation, ReplyTo = reply, ContentType = "application/json",
            Headers = new Dictionary<string, object?>
            {
                ["sf-version"] = "1", ["sf-ex-saga-id"] = Guid.NewGuid().ToString("N"),
                ["sf-ex-correlation-id"] = correlation, ["sf-ex-domain"] = "contract-test",
                ["sf-ex-process"] = "contract-process", ["sf-ex-process-version"] = "1.0",
                ["sf-ex-step-id"] = step, ["sf-ex-command-type"] = type,
                ["sf-ex-execution-id"] = Guid.NewGuid().ToString("N"), ["sf-ex-command-id"] = commandId,
                ["sf-ex-kind"] = "forward", ["sf-ex-created-at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["sf-ex-reply-to"] = reply
            }
        };
    }

    private static async Task Wait(Func<Task<bool>> ready)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < until)
        {
            if (await ready()) return;
            await Task.Delay(50);
        }
        Assert.Fail("Deferred participant did not reach the expected broker/API state.");
    }
}
