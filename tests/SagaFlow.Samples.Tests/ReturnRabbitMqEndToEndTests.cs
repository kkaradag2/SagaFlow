using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.State;
using SagaFlow.Returns;
using SagaFlow.Warehouse;
using SagaFlow.Order.Domain;
using SagaFlow.Order.Contracts.Responses;
using SagaFlow.Core.Messages;
using SagaFlow.Engine.Coordination;
using SagaFlow.Engine.Messaging;
using System.Text.Json;

namespace SagaFlow.Samples.Tests;

[CollectionDefinition("sample-rabbitmq-e2e")]
public sealed class SampleRabbitMqCollection;

[Collection("sample-rabbitmq-e2e")]
public sealed class ReturnRabbitMqEndToEndTests
{
    [SampleRabbitMqFact]
    public async Task AcceptedAndRejectedReturnsRunThroughIndependentParticipants()
    {
        SampleRabbitMqTestIsolation.RequireVirtualHost();
        await using var returns = new WebApplicationFactory<ReturnProgram>();
        await using var warehouse = new WebApplicationFactory<WarehouseProgram>();
        using var returnClient = returns.CreateClient();
        using var warehouseClient = warehouse.CreateClient();
        await using var connection = await ConnectAsync();
        var workers = StartWorkers();
        try
        {
            await WaitForConsumersAsync(connection);
            foreach (var decision in new[] { "accept", "reject" })
            {
                var orderId = Guid.NewGuid();
                var created = await CreateReturnAsync(returnClient, orderId);
                await WaitForInspectionAsync(warehouseClient, created.ReturnId);
                if (decision == "accept")
                {
                    var before = (await returns.Services.GetRequiredService<ISagaStateStore>().FindAsync(created.SagaInstanceId))!;
                    using var scope = returns.Services.CreateScope();
                    await Assert.ThrowsAsync<InvalidSagaOutcomeException>(() =>
                        scope.ServiceProvider.GetRequiredService<OutcomeEventReceiver>().ReceiveAsync(new SagaOutcomeEvent {
                            Name = "InspectReturnCompleted", IsSuccess = true,
                            Headers = new() { SagaInstanceId = created.SagaInstanceId.ToString("N"),
                                CorrelationId = "wrong", StepId = "warehouse" },
                            Result = JsonSerializer.SerializeToElement(new { accepted = true }) }));
                    var after = (await returns.Services.GetRequiredService<ISagaStateStore>().FindAsync(created.SagaInstanceId))!;
                    Assert.Equal(before.Revision, after.Revision);
                    foreach (var malformed in new[] { "{}", "{\"accepted\":null}", "{\"accepted\":\"false\"}", "{\"accepted\":0}", "{\"accepted\":[]}", "{\"accepted\":{}}" })
                    {
                        using var result = JsonDocument.Parse(malformed);
                        await Assert.ThrowsAsync<InvalidSagaOutcomeException>(() =>
                            scope.ServiceProvider.GetRequiredService<OutcomeEventReceiver>().ReceiveAsync(new SagaOutcomeEvent {
                                Name = "InspectReturnCompleted", IsSuccess = true,
                                Headers = new() { SagaInstanceId = created.SagaInstanceId.ToString("N"),
                                    CorrelationId = before.CorrelationId, StepId = "warehouse" },
                                Result = result.RootElement.Clone() }));
                        Assert.Equal(before.Revision,
                            (await returns.Services.GetRequiredService<ISagaStateStore>().FindAsync(created.SagaInstanceId))!.Revision);
                    }
                }
                var response = await warehouseClient.PostAsJsonAsync($"/inspections/{created.ReturnId}/decision", new { decision });
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var state = await WaitForTerminalAsync(returns.Services, created.SagaInstanceId);
                Assert.Equal(SagaStatus.Completed, state.Status);
                Assert.Equal(orderId, created.OrderId);
                Assert.Equal(decision == "accept"
                    ? new[] { "warehouse", "payment", "inventory", "acceptedNotification" }
                    : ["warehouse", "rejectedNotification"],
                    state.Steps.Where(x => x.ExecutionOrder.HasValue).OrderBy(x => x.ExecutionOrder).Select(x => x.StepId));
                var business = await returnClient.GetFromJsonAsync<ReturnView>($"/returns/{created.ReturnId}");
                Assert.Equal(decision == "accept" ? "Accepted" : "Rejected", business!.BusinessStatus);
                Assert.Equal(state.CorrelationId, business.SagaCorrelationId);
                if (decision == "accept")
                {
                    await StopWorkersAsync(workers);
                    workers = StartWorkers();
                    await WaitForConsumersAsync(connection);
                }
            }
        }
        finally { await StopWorkersAsync(workers); }
    }

    [SampleRabbitMqFact]
    public async Task RestockFailureAfterRefundRequiresManualReview()
    {
        SampleRabbitMqTestIsolation.RequireVirtualHost();
        await using var returns = new WebApplicationFactory<ReturnProgram>();
        await using var warehouse = new WebApplicationFactory<WarehouseProgram>();
        using var returnClient = returns.CreateClient();
        using var warehouseClient = warehouse.CreateClient();
        await using var connection = await ConnectAsync();
        var workers = StartWorkers();
        try
        {
            await WaitForConsumersAsync(connection);
            var created = await CreateReturnAsync(returnClient, Guid.NewGuid(), "inventory");
            await WaitForInspectionAsync(warehouseClient, created.ReturnId);
            Assert.Equal(HttpStatusCode.OK,
                (await warehouseClient.PostAsJsonAsync($"/inspections/{created.ReturnId}/decision", new { decision = "accept" })).StatusCode);
            var state = await WaitForTerminalAsync(returns.Services, created.SagaInstanceId);
            Assert.Equal(SagaStepStatus.Completed, state.Steps.Single(x => x.StepId == "payment").Status);
            Assert.Equal(SagaStepStatus.Failed, state.Steps.Single(x => x.StepId == "inventory").Status);
            Assert.Empty(state.Compensations); // Financial refund is not reversible in this PoC.
            var view = await returnClient.GetFromJsonAsync<ReturnView>($"/returns/{created.ReturnId}");
            Assert.Equal("ManualReview", view!.BusinessStatus);
        }
        finally { await StopWorkersAsync(workers); }
    }

    [SampleRabbitMqFact]
    public async Task TechnicalFailuresDoNotPretendToReverseRefund()
    {
        SampleRabbitMqTestIsolation.RequireVirtualHost();
        await using var returns = new WebApplicationFactory<ReturnProgram>();
        await using var warehouse = new WebApplicationFactory<WarehouseProgram>();
        using var returnClient = returns.CreateClient();
        using var warehouseClient = warehouse.CreateClient();
        await using var connection = await ConnectAsync();
        var workers = StartWorkers();
        try
        {
            await WaitForConsumersAsync(connection);
            foreach (var failure in new[] { "warehouse", "payment", "notification", "notification-reject" })
            {
                var created = await CreateReturnAsync(returnClient, Guid.NewGuid(),
                    failure == "notification-reject" ? "notification" : failure);
                if (failure != "warehouse") {
                    await WaitForInspectionAsync(warehouseClient, created.ReturnId);
                    Assert.Equal(HttpStatusCode.OK,
                        (await warehouseClient.PostAsJsonAsync($"/inspections/{created.ReturnId}/decision",
                            new { decision = failure == "notification-reject" ? "reject" : "accept" })).StatusCode);
                }
                var state = await WaitForTerminalAsync(returns.Services, created.SagaInstanceId);
                Assert.NotEqual(SagaStatus.Completed, state.Status);
                var view = await returnClient.GetFromJsonAsync<ReturnView>($"/returns/{created.ReturnId}");
                Assert.Equal(failure == "notification" ? "ManualReview" : "Failed", view!.BusinessStatus);
            }
        }
        finally { await StopWorkersAsync(workers); }
    }

    [SampleRabbitMqFact]
    public async Task OrderAndReturnShareParticipantsWithoutSharingSagaState()
    {
        SampleRabbitMqTestIsolation.RequireVirtualHost();
        await using var orders = new WebApplicationFactory<global::Program>();
        await using var returns = new WebApplicationFactory<ReturnProgram>();
        await using var warehouse = new WebApplicationFactory<WarehouseProgram>();
        using var orderClient = orders.CreateClient();
        using var returnClient = returns.CreateClient();
        using var warehouseClient = warehouse.CreateClient();
        await using var connection = await ConnectAsync();
        var workers = StartWorkers(includeShipment: true);
        try
        {
            await WaitForConsumersAsync(connection, includeOrder: true);
            var orderResponse = await orderClient.PostAsJsonAsync("/orders", new CreateOrderRequest("customer",
                [new CreateOrderItem("sku", 1, 25m)]));
            Assert.Equal(HttpStatusCode.Created, orderResponse.StatusCode);
            var order = (await orderResponse.Content.ReadFromJsonAsync<SuccessResponse<CreateOrderResult>>())!.Data;
            var returned = await CreateReturnAsync(returnClient, order.OrderId);
            await WaitForInspectionAsync(warehouseClient, returned.ReturnId);
            Assert.Equal(HttpStatusCode.OK,
                (await warehouseClient.PostAsJsonAsync($"/inspections/{returned.ReturnId}/decision", new { decision = "accept" })).StatusCode);
            Assert.Equal(SagaStatus.Completed, (await WaitForTerminalAsync(orders.Services, order.SagaInstanceId)).Status);
            Assert.Equal(SagaStatus.Completed, (await WaitForTerminalAsync(returns.Services, returned.SagaInstanceId)).Status);
            Assert.NotEqual(order.SagaInstanceId, returned.SagaInstanceId);
            Assert.Null(await orders.Services.GetRequiredService<ISagaStateStore>().FindAsync(returned.SagaInstanceId));
            Assert.Null(await returns.Services.GetRequiredService<ISagaStateStore>().FindAsync(order.SagaInstanceId));
            var definitions = await returnClient.GetStringAsync("/sagaflow/api/definitions");
            Assert.Contains("return-process", definitions);
            Assert.Contains("warehouse", definitions);
            Assert.Contains("rejectedNotification", definitions);
            Assert.Equal(HttpStatusCode.OK, (await returnClient.GetAsync("/sagaflow")).StatusCode);
            var detail = await returnClient.GetStringAsync($"/sagaflow/api/instances/{returned.SagaInstanceId:N}");
            Assert.Contains("acceptedNotification", detail);
        }
        finally { await StopWorkersAsync(workers); }
    }

    private sealed record ReturnView(string BusinessStatus, string? SagaCorrelationId);

    private static async Task<ReturnCreated> CreateReturnAsync(HttpClient client, Guid orderId, string? failureAt = null)
    {
        var response = await client.PostAsJsonAsync("/returns", new CreateReturnRequest(orderId, "customer", 25m,
            [new ReturnItem("sku", 2)], failureAt));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ReturnCreated>())!;
    }

    private static async Task WaitForInspectionAsync(HttpClient client, Guid returnId)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < until)
        {
            if ((await client.GetAsync($"/inspections/{returnId}")).StatusCode == HttpStatusCode.OK) return;
            await Task.Delay(100);
        }
        Assert.Fail("Warehouse did not receive inspection command.");
    }

    private static async Task<SagaInstance> WaitForTerminalAsync(IServiceProvider services, Guid sagaId)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < until)
        {
            var state = await services.GetRequiredService<ISagaStateStore>().FindAsync(sagaId);
            if (state?.Status is SagaStatus.Completed or SagaStatus.Compensated or SagaStatus.Failed) return state;
            await Task.Delay(100);
        }
        throw new Xunit.Sdk.XunitException("Return Saga did not reach a terminal state.");
    }

    private static List<Process> StartWorkers(bool includeShipment = false) => includeShipment
        ? [Start("SagaFlow.Payment"), Start("SagaFlow.Inventory"), Start("SagaFlow.Notification"), Start("SagaFlow.Shipment")]
        : [Start("SagaFlow.Payment"), Start("SagaFlow.Inventory"), Start("SagaFlow.Notification")];

    private static Process Start(string name)
    {
        var root = FindRoot();
        var project = Path.Combine(root, "samples", "Participants", name);
        var config = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        var isolated = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", name,
            new DirectoryInfo(AppContext.BaseDirectory).Name, name + ".dll"));
        var assembly = File.Exists(isolated) ? isolated : Path.Combine(project, "bin", config, "net10.0", name + ".dll");
        var info = new ProcessStartInfo("dotnet", assembly) {
            WorkingDirectory = project, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        return Process.Start(info) ?? throw new InvalidOperationException($"Could not start {name}.");
    }

    private static async Task StopWorkersAsync(IEnumerable<Process> workers)
    {
        foreach (var worker in workers) {
            if (!worker.HasExited) worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync(); worker.Dispose(); }
    }

    private static async Task<IConnection> ConnectAsync()
    {
        var vhost = SampleRabbitMqTestIsolation.RequireVirtualHost();
        var factory = new ConnectionFactory {
            HostName = Environment.GetEnvironmentVariable("RabbitMq__HostName") ?? "localhost",
            Port = int.TryParse(Environment.GetEnvironmentVariable("RabbitMq__Port"), out var port) ? port : 5672,
            UserName = Environment.GetEnvironmentVariable("RabbitMq__UserName") ?? "sagaflow",
            Password = Environment.GetEnvironmentVariable("RabbitMq__Password") ?? "sagaflow-dev",
            VirtualHost = vhost };
        return await factory.CreateConnectionAsync();
    }

    private static async Task WaitForConsumersAsync(IConnection connection, bool includeOrder = false)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until) {
            await using var channel = await connection.CreateChannelAsync();
            var ready = true;
            foreach (var queue in new[] { "warehouse", "payment", "inventory", "acceptedNotification", "rejectedNotification", "return.outcomes" }
                .Concat(includeOrder ? ["shipment", "notification", "order.outcomes"] : [])) {
                try { ready &= await channel.ConsumerCountAsync(queue) > 0; } catch { ready = false; break; } }
            if (ready) return;
            await Task.Delay(100);
        }
        Assert.Fail("Return consumers did not become ready.");
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SagaFlow.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException();
    }
}
