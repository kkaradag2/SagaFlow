using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.State;
using SagaFlow.Order.Contracts.Responses;
using SagaFlow.Order.Domain;

namespace SagaFlow.Samples.Tests;

public sealed class SampleRabbitMqEndToEndTests
{
    [SampleRabbitMqFact]
    public async Task OrdersCompleteAcrossIndependentParticipantsAndWorkersCanRestart()
    {
        await using var factory = new WebApplicationFactory<global::Program>();
        using var client = factory.CreateClient();
        await using var broker = await CreateConnectionAsync();
        var workers = new List<Process>();

        try
        {
            await RemoveLegacyCommandBindingsAsync(broker);
            workers = StartWorkers();
            await WaitForConsumersAsync(broker);
            await AssertCommandNameIsUnroutableAsync(broker);

            var firstBatch = await Task.WhenAll(
                CreateOrderAsync(client, "customer-1", "product-1"),
                CreateOrderAsync(client, "customer-2", "product-2"));
            await WaitForCompletionAsync(factory.Services, firstBatch.Select(result => result.SagaInstanceId));
            Assert.Equal(2, firstBatch.Select(result => result.SagaInstanceId).Distinct().Count());

            await StopWorkersAsync(workers);
            workers = StartWorkers();
            await WaitForConsumersAsync(broker);

            var afterRestart = await CreateOrderAsync(client, "customer-3", "product-3");
            await WaitForCompletionAsync(factory.Services, [afterRestart.SagaInstanceId]);
        }
        finally
        {
            await StopWorkersAsync(workers);
        }
    }

    private static async Task<CreateOrderResult> CreateOrderAsync(
        HttpClient client,
        string customerId,
        string productId)
    {
        var response = await client.PostAsJsonAsync(
            "/orders",
            new CreateOrderRequest(customerId, [new(productId, 2, 12.50m)]));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SuccessResponse<CreateOrderResult>>();
        return body!.Data;
    }

    private static async Task WaitForCompletionAsync(IServiceProvider services, IEnumerable<Guid> sagaIds)
    {
        var ids = sagaIds.ToArray();
        var timeout = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < timeout)
        {
            var states = new List<SagaInstance>();
            foreach (var id in ids)
            {
                var state = await services.GetRequiredService<ISagaStateStore>().FindAsync(id);
                if (state is not null) states.Add(state);
            }

            if (states.Count == ids.Length && states.All(state => state.Status == SagaStatus.Completed))
            {
                Assert.Equal(states.Count, states.Select(state => state.CorrelationId).Distinct().Count());
                Assert.All(states, state =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(state.CorrelationId));
                    Assert.All(state.Steps, step =>
                    {
                        Assert.Equal(SagaStepStatus.Completed, step.Status);
                        Assert.NotNull(step.Result);
                    });
                });
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail("Sample Saga instances did not complete within 20 seconds.");
    }

    private static List<Process> StartWorkers() =>
    [
        StartWorker("SagaFlow.Payment"),
        StartWorker("SagaFlow.Inventory"),
        StartWorker("SagaFlow.Shipment"),
        StartWorker("SagaFlow.Notification")
    ];

    private static Process StartWorker(string projectName)
    {
        var root = FindRepositoryRoot();
        var projectDirectory = Path.Combine(root, "samples", "Participants", projectName);
        var assembly = Path.Combine(projectDirectory, "bin", "Debug", "net10.0", $"{projectName}.dll");
        var startInfo = new ProcessStartInfo("dotnet", assembly)
        {
            WorkingDirectory = projectDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {projectName}.");
    }

    private static async Task StopWorkersAsync(IEnumerable<Process> workers)
    {
        foreach (var worker in workers)
        {
            if (!worker.HasExited) worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync();
            worker.Dispose();
        }
    }

    private static async Task<IConnection> CreateConnectionAsync()
    {
        var factory = new ConnectionFactory
        {
            HostName = "localhost",
            Port = 5672,
            UserName = "sagaflow",
            Password = "sagaflow-dev",
            VirtualHost = "/",
            ClientProvidedName = "sagaflow-sample-tests"
        };
        return await factory.CreateConnectionAsync();
    }

    private static async Task WaitForConsumersAsync(IConnection connection)
    {
        var queues = new[] { "payment", "inventory", "shipment", "notification", "order.outcomes" };
        var timeout = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < timeout)
        {
            await using var channel = await connection.CreateChannelAsync();
            var ready = true;
            foreach (var queue in queues)
            {
                try { ready &= await channel.ConsumerCountAsync(queue) > 0; }
                catch { ready = false; break; }
            }
            if (ready) return;
            await Task.Delay(100);
        }
        Assert.Fail("Participant consumers did not become ready within 15 seconds.");
    }

    private static async Task RemoveLegacyCommandBindingsAsync(IConnection connection)
    {
        var bindings = new Dictionary<string, string[]>
        {
            ["payment"] = ["AuthorizePayment", "RefundPayment"],
            ["inventory"] = ["ReserveInventory", "ReleaseInventory"],
            ["shipment"] = ["CreateShipment", "CancelShipment"],
            ["notification"] = ["SendOrderNotification"]
        };
        await using var channel = await connection.CreateChannelAsync();
        foreach (var (queue, routingKeys) in bindings)
        foreach (var routingKey in routingKeys)
            await channel.QueueUnbindAsync(queue, "sagaflow.commands", routingKey);
    }

    private static async Task AssertCommandNameIsUnroutableAsync(IConnection connection)
    {
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true));
        await Assert.ThrowsAnyAsync<PublishException>(() => channel.BasicPublishAsync(
            exchange: "sagaflow.commands",
            routingKey: "AuthorizePayment",
            mandatory: true,
            basicProperties: new BasicProperties { ContentType = "application/json" },
            body: "{}"u8.ToArray()).AsTask());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SagaFlow.slnx")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("SagaFlow repository root was not found.");
    }
}
