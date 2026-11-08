using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using RabbitMQ.Client;
using SagaFlow.ManagerApproval;

namespace SagaFlow.Samples.Tests;

public sealed partial class DeferredWireContractTests
{
    private static async Task<IConnection> ConnectQuarantineTest()
    {
        var factory = new ConnectionFactory
        {
            HostName = Environment.GetEnvironmentVariable("SAGAFLOW_RABBITMQ_HOST") ?? "localhost",
            UserName = Environment.GetEnvironmentVariable("SAGAFLOW_RABBITMQ_USERNAME")!,
            Password = Environment.GetEnvironmentVariable("SAGAFLOW_RABBITMQ_PASSWORD")!,
            VirtualHost = SampleRabbitMqTestIsolation.RequireVirtualHost()
        };
        return await factory.CreateConnectionAsync();
    }

    [SampleRabbitMqFact]
    public async Task LegacyCommandsAreQuarantinedWithTtlRemovedAndNoBusinessOperationWhileV1Continues()
    {
        await using var connection = await ConnectQuarantineTest();
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true));
        await channel.QueueDeclareAsync("managerApproval", true, false, false);
        await channel.QueueDeclareAsync("managerApproval.quarantine", true, false, false);
        await using var manager = new WebApplicationFactory<ManagerApprovalProgram>();
        using var api = manager.CreateClient();
        await Wait(async () => await channel.ConsumerCountAsync("managerApproval") == 1);
        foreach (var version in new string?[] { null, "0", "2" })
        {
            var body = "{\"name\":\"RequestManagerApproval\",\"payload\":{\"id\":17},\"headers\":{}}"u8.ToArray();
            var properties = new BasicProperties { Type = "SagaCommand", MessageId = Guid.NewGuid().ToString("N"),
                ContentType = "application/json", Expiration = "1000", Headers = new Dictionary<string, object?>() };
            if (version is not null) properties.Headers["sf-version"] = version;
            await channel.BasicPublishAsync("sagaflow.commands", "managerApproval", true, properties, body);
            await Wait(async () => await channel.MessageCountAsync("managerApproval.quarantine") == 1);
            await Task.Delay(1200); // Original TTL must not delete the quarantined copy.
            var preserved = await channel.BasicGetAsync("managerApproval.quarantine", true);
            Assert.NotNull(preserved);
            Assert.Equal(body, preserved.Body.ToArray());
            Assert.Equal(properties.MessageId, preserved.BasicProperties.MessageId);
            Assert.Equal(properties.Type, preserved.BasicProperties.Type);
            Assert.Null(preserved.BasicProperties.Expiration);
            Assert.Equal("1000", Encoding.UTF8.GetString((byte[])preserved.BasicProperties.Headers!["sf-quarantine-original-expiration"]!));
            Assert.Equal(0u, await channel.MessageCountAsync("managerApproval"));
            Assert.Equal(1u, await channel.ConsumerCountAsync("managerApproval"));
            using var pending = JsonDocument.Parse(await api.GetStringAsync("/approvals/pending"));
            Assert.Empty(pending.RootElement.EnumerateArray());
        }
        var id = Guid.NewGuid();
        var reply = (await channel.QueueDeclareAsync()).QueueName;
        await channel.BasicPublishAsync("sagaflow.commands", "managerApproval", true,
            CommandProperties("RequestManagerApproval", "managerApproval", reply),
            JsonSerializer.SerializeToUtf8Bytes(new { id, totalQuantity = 1001 }));
        await Wait(async () => (await api.GetAsync($"/approvals/{id}")).StatusCode == HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsJsonAsync($"/approvals/{id}/decision", new { decision = "approve" })).StatusCode);
        BasicGetResult? outcome = null;
        await Wait(async () => (outcome = await channel.BasicGetAsync(reply, true)) is not null);
        using var result = JsonDocument.Parse(outcome!.Body);
        Assert.True(result.RootElement.GetProperty("approved").GetBoolean());
        Assert.Single(result.RootElement.EnumerateObject());
        Assert.Equal(0u, await channel.ConsumerCountAsync("managerApproval.quarantine"));
    }

    [SampleRabbitMqFact]
    public async Task ParticipantQuarantineFailureStopsConsumptionAndLeavesOriginalForManualRecovery()
    {
        await using var connection = await ConnectQuarantineTest();
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true));
        await channel.QueueDeclareAsync("managerApproval", true, false, false);
        await channel.QueueDeclareAsync("managerApproval.quarantine", true, false, false);
        await using var manager = new WebApplicationFactory<ManagerApprovalProgram>();
        using var api = manager.CreateClient();
        await Wait(async () => await channel.ConsumerCountAsync("managerApproval") == 1);
        await channel.QueueDeleteAsync("managerApproval.quarantine");
        await channel.BasicPublishAsync("sagaflow.commands", "managerApproval", true,
            new BasicProperties { Type = "SagaCommand", MessageId = "retained-command", ContentType = "application/json" },
            "{\"legacy\":true}"u8.ToArray());
        await Wait(async () => await channel.ConsumerCountAsync("managerApproval") == 0 && await channel.MessageCountAsync("managerApproval") == 1);
        await Task.Delay(250);
        Assert.Equal(0u, await channel.ConsumerCountAsync("managerApproval"));
        var retained = await channel.BasicGetAsync("managerApproval", true);
        Assert.NotNull(retained); Assert.True(retained.Redelivered);
        Assert.Equal("retained-command", retained.BasicProperties.MessageId);
        using var pending = JsonDocument.Parse(await api.GetStringAsync("/approvals/pending"));
        Assert.Empty(pending.RootElement.EnumerateArray());
        await channel.QueueDeclareAsync("managerApproval.quarantine", true, false, false);
    }
}
