using RabbitMQ.Client;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Engine.Messaging;
using SagaFlow.Engine.Coordination;
using SagaFlow.Hosting.Dashboard;
using SagaFlow.ManagerApproval;
using SagaFlow.Order.Services;
using SagaFlow.Procurement;

namespace SagaFlow.Samples.Tests;

[Collection("sample-rabbitmq-e2e")]
public sealed class ProcurementRabbitMqTests
{
    // Test-only rollback fault: real participant compensation still runs before this local application failure.
    public sealed class RollbackFault { public bool Enabled { get; set; } }
    public sealed class RequestFault { public bool Enabled { get; set; } }
    public sealed class ControlledOrderStep(ISagaFlow flow, RequestFault fault) : ILocalSagaStepHandler
    {
        public Task ExecuteAsync(SagaCommand command, CancellationToken ct) => !fault.Enabled
            ? new ProcurementOrderStep(flow).ExecuteAsync(command, ct) : RejectableRequest(command, ct);
        private async Task RejectableRequest(SagaCommand command, CancellationToken ct)
        {
            if (command.Name == "InvokeOrder") await flow.InvokeAsync("order-process", new
            {
                purchaseRequestId = command.Payload.GetProperty("id").GetGuid(),
                order = new { customerId = "", items = Array.Empty<object>() }
            }, ct);
        }
    }
    public sealed class ControlledRollback(IOrderService orders, RollbackFault fault) : SagaRollbackHandler<JsonElement>
    {
        public override Task RollbackAsync(JsonElement payload, CancellationToken cancellationToken) => fault.Enabled
            ? throw new IOException("Controlled local rollback failure")
            : orders.CancelAsync(payload.GetProperty("id").GetGuid(), cancellationToken);
    }

    [SampleRabbitMqFact]
    public async Task ApprovalDrivesOrderOwnedExecutionAndTerminalPropagationAcrossIndependentHosts()
    {
        SampleRabbitMqTestIsolation.RequireVirtualHost();
        var fault = new RollbackFault();
        var requestFault = new RequestFault();
        await using var order = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder => builder
            .UseSetting("RabbitMq:EnableProcessInvocations", "true")
            .UseSetting("SagaFlow:PublishDefinitions", "true")
            .UseSetting("SagaFlow:PublishBigPictureSources", "true")
            .ConfigureServices(services =>
            {
                services.AddSingleton(fault);
                services.AddScoped<SagaRollbackHandler<JsonElement>, ControlledRollback>();
            }));
        ListenOptions? orderHttp = null, orderGrpc = null;
        order.UseKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0, listen => { listen.Protocols = HttpProtocols.Http1; orderHttp = listen; });
            options.Listen(IPAddress.Loopback, 0, listen => { listen.Protocols = HttpProtocols.Http2; orderGrpc = listen; });
        });
        using var orderClient = order.CreateClient();
        orderClient.BaseAddress = new Uri($"http://127.0.0.1:{orderHttp!.IPEndPoint!.Port}");
        var grpcEndpoint = $"http://127.0.0.1:{orderGrpc!.IPEndPoint!.Port}";
        var directory = Path.Combine(Path.GetTempPath(), "sagaflow-procurement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var workers = new List<Process>();
        try
        {
            // Same production definition, with only its test endpoint replaced by the real ephemeral gRPC listener.
            await File.WriteAllTextAsync(Path.Combine(directory, "procurement-process.yaml"),
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "procurement-process.yaml"))
                    .Replace("https://localhost:50823", grpcEndpoint, StringComparison.Ordinal));
            await using var procurement = new WebApplicationFactory<ProcurementProgram>().WithWebHostBuilder(builder => builder
                .UseSetting("SagaFlow:DefinitionDirectory", directory)
                .UseSetting("SagaFlow:Discovery:AllowedEndpoints:0", grpcEndpoint)
                .UseSetting("SagaFlow:Discovery:AllowInsecureHttp", "true")
                .ConfigureServices(services =>
                {
                    services.RemoveAll<ILocalSagaStepHandler>(); services.AddSingleton(requestFault);
                    services.AddScoped<ILocalSagaStepHandler, ControlledOrderStep>();
                }));
            procurement.UseKestrel(0);
            using var client = procurement.CreateClient();
            await using var approval = new WebApplicationFactory<ManagerApprovalProgram>();
            using var approvals = approval.CreateClient();
            await using var broker = await SampleRabbitMqEndToEndTests.CreateConnectionAsync();
            workers = SampleRabbitMqEndToEndTests.StartWorkers();
            await SampleRabbitMqEndToEndTests.WaitForConsumersAsync(broker, includeApproval: true);
            await using var wire = await broker.CreateChannelAsync(new RabbitMQ.Client.CreateChannelOptions(true, true));
            var auditQueue = (await wire.QueueDeclareAsync()).QueueName;
            await wire.QueueBindAsync(auditQueue, "sagaflow.commands", "managerApproval");
            var sourceStore = procurement.Services.GetRequiredService<ISagaStateStore>();
            var targetStore = order.Services.GetRequiredService<ISagaStateStore>();
            Assert.NotSame(sourceStore, targetStore);

            using var graph = JsonDocument.Parse(await client.GetStringAsync("/sagaflow/api/definition-graph?domain=procurement&process=procurement-process&version=1.0"));
            Assert.True(graph.RootElement.GetProperty("nodes").EnumerateArray().Any(n => n.GetProperty("domain").GetString() == "order"), graph.RootElement.GetRawText());
            Assert.Contains(graph.RootElement.GetProperty("edges").EnumerateArray(), e => e.GetProperty("relationship").GetString() == "invokes");
            // Existing Order -> Return relationship is retained; Return is deliberately not started by this fixture.
            Assert.Contains(graph.RootElement.GetProperty("edges").EnumerateArray(), e => e.GetProperty("relationship").GetString() == "related");

            foreach (var scenario in new[] { "reject", "business-reject", "complete", "compensate", "fail" })
            {
                fault.Enabled = scenario == "fail";
                requestFault.Enabled = scenario == "business-reject";
                var targetCount = (await targetStore.ListAsync()).Count;
                var response = await client.PostAsJsonAsync("/purchase-requests", new CreatePurchaseRequest("employee", "company",
                    [new("product", 2, 12.50m)], scenario is "compensate" or "fail" ? "notification" : null));
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                var created = (await response.Content.ReadFromJsonAsync<PurchaseCreated>())!;
                await Until(async () => (await approvals.GetAsync($"/approvals/{created.PurchaseRequestId}")).StatusCode == HttpStatusCode.OK);
                using var pending = JsonDocument.Parse(await approvals.GetStringAsync($"/approvals/{created.PurchaseRequestId}"));
                Assert.Equal(created.PurchaseRequestId, pending.RootElement.GetProperty("orderId").GetGuid());
                Assert.False(pending.RootElement.TryGetProperty("replyTo", out _));
                Assert.False(pending.RootElement.TryGetProperty("sagaInstanceId", out _));
                var approvalCommandWire = await wire.BasicGetAsync(auditQueue, true);
                Assert.NotNull(approvalCommandWire);
                Assert.Equal("procurement.outcomes", approvalCommandWire.BasicProperties.ReplyTo);
                Assert.Equal(created.SagaInstanceId.ToString("N"),
                    System.Text.Encoding.UTF8.GetString((byte[])approvalCommandWire.BasicProperties.Headers!["sf-ex-saga-id"]!));
                using (var businessBody = JsonDocument.Parse(approvalCommandWire.Body))
                    Assert.Equal(created.PurchaseRequestId, businessBody.RootElement.GetProperty("id").GetGuid());
                var decision = scenario == "reject" ? "reject" : "approve";
                Assert.Equal(HttpStatusCode.OK, (await approvals.PostAsJsonAsync($"/approvals/{created.PurchaseRequestId}/decision", new { decision })).StatusCode);
                Assert.Equal(HttpStatusCode.Conflict, (await approvals.PostAsJsonAsync($"/approvals/{created.PurchaseRequestId}/decision", new { decision })).StatusCode);
                await Until(async () => (await sourceStore.FindAsync(created.SagaInstanceId))?.Status is SagaStatus.Completed or SagaStatus.Compensated or SagaStatus.Failed);
                var source = (await sourceStore.FindAsync(created.SagaInstanceId))!;
                var view = (await client.GetFromJsonAsync<PurchaseView>($"/purchase-requests/{created.PurchaseRequestId}"))!;
                Assert.Equal(scenario == "reject" ? "Rejected" : scenario == "complete" ? "Fulfilled" : "Failed", view.Status);
                var step = source.Steps.Single(s => s.StepId == "invokeOrder");
                if (scenario == "reject")
                {
                    Assert.Null(step.Invocation); Assert.Equal(targetCount, (await targetStore.ListAsync()).Count); continue;
                }
                Assert.Null(step.Result);
                Assert.NotNull(step.CompletedAt);
                Assert.Equal(scenario == "complete" ? SagaStepStatus.Completed : SagaStepStatus.Failed, step.Status);
                var request = step.Invocation!;
                if (scenario == "business-reject")
                {
                    Assert.Equal(ExternalProcessStatus.Rejected, step.InvocationProgress!.Status);
                    Assert.False(string.IsNullOrWhiteSpace(step.InvocationProgress.RejectionReason));
                    using var rejectedDetail = JsonDocument.Parse(await client.GetStringAsync($"/sagaflow/api/instances/{source.Id}"));
                    var rejectedStep = rejectedDetail.RootElement.GetProperty("steps").EnumerateArray().Single(item => item.GetProperty("stepName").GetString() == "invokeOrder");
                    Assert.Equal(JsonValueKind.Null, rejectedStep.GetProperty("result").ValueKind);
                    Assert.Equal(step.InvocationProgress.RejectionReason, rejectedStep.GetProperty("invocation").GetProperty("rejectionReason").GetString());
                    using var rejectedRelationships = JsonDocument.Parse(await client.GetStringAsync($"/sagaflow/api/instances/{source.Id}/relationships"));
                    Assert.Equal(step.InvocationProgress.RejectionReason, rejectedRelationships.RootElement.GetProperty("items")[0].GetProperty("rejectionReason").GetString());
                    Assert.Null(step.InvocationProgress.TargetSagaInstanceId);
                    Assert.Equal(targetCount, (await targetStore.ListAsync()).Count);
                    Assert.Equal(HttpStatusCode.NotFound, (await orderClient.GetAsync($"/orders/invocations/{request.Identity.InvocationId}")).StatusCode);
                    continue;
                }
                var target = (await targetStore.FindAsync(step.InvocationProgress!.TargetSagaInstanceId!))!;
                var expected = scenario == "complete" ? SagaStatus.Completed : scenario == "compensate" ? SagaStatus.Compensated : SagaStatus.Failed;
                Assert.Equal(expected, target.Status); Assert.Equal(expected.ToString(), view.LastKnownOrderStatus);
                Assert.NotEqual(source.CorrelationId, target.CorrelationId);
                Assert.Equal(source.CorrelationId, request.Identity.CorrelationId);
                Assert.Equal("procurement", request.Identity.Source.Domain); Assert.Equal("order", request.Identity.Target.Domain);
                Assert.Equal(request.Identity, target.IncomingInvocation!.Identity);
                Assert.Null(await sourceStore.FindAsync(target.Id)); Assert.Null(await targetStore.FindAsync(source.Id));
                var link = (await orderClient.GetFromJsonAsync<IncomingOrderLink>($"/orders/invocations/{request.Identity.InvocationId}"))!;
                Assert.Equal(created.PurchaseRequestId, link.PurchaseRequestId);
                Assert.Equal(target.Id, link.TargetSagaInstanceId);
                Assert.Equal(scenario == "compensate" ? SagaFlow.Order.Domain.OrderStatus.Cancelled : SagaFlow.Order.Domain.OrderStatus.Created, link.Order.Status);
                Assert.Equal(4, new[] { created.PurchaseRequestId, created.SagaInstanceId, Guid.Parse(target.Id), link.Order.Id }.Distinct().Count());
                Assert.Equal(link.Order.Id, target.Context.GetProperty("id").GetGuid());
                Assert.Equal(created.PurchaseRequestId, target.IncomingInvocation.Payload!.Value.GetProperty("purchaseRequestId").GetGuid());
                Assert.NotNull(target.Steps.Single(s => s.StepId == "payment").Result);
                Assert.NotNull(target.Steps.Single(s => s.StepId == "notification").Result);
                var sourceRows = (await client.GetFromJsonAsync<InstanceRelationshipPage>($"/sagaflow/api/instances/{source.Id}/relationships"))!;
                var targetRows = (await orderClient.GetFromJsonAsync<InstanceRelationshipPage>($"/sagaflow/api/instances/{target.Id}/relationships"))!;
                Assert.Equal(request.Identity.InvocationId, Assert.Single(sourceRows.Items).InvocationId);
                var incoming = Assert.Single(targetRows.Items); Assert.Equal("Target", incoming.Perspective);
                Assert.Equal(request.Identity.InvocationId, incoming.InvocationId); Assert.Equal(target.CorrelationId, incoming.TargetCorrelationId);
                using var detail = JsonDocument.Parse(await orderClient.GetStringAsync($"/sagaflow/api/instances/{target.Id}"));
                Assert.Equal("External", detail.RootElement.GetProperty("origin").GetString());

                // Deterministic duplicate delivery through the real receivers, not DTO mapping or hand-edited state.
                await using var targetScope = order.Services.CreateAsyncScope();
                await targetScope.ServiceProvider.GetRequiredService<ProcessInvocationReceiver>().ReceiveAsync(request);
                Assert.Equal(targetCount + 1, (await targetStore.ListAsync()).Count);
                Assert.Equal(link.Order.Id, (await orderClient.GetFromJsonAsync<IncomingOrderLink>($"/orders/invocations/{request.Identity.InvocationId}"))!.Order.Id);
                await using var sourceScope = procurement.Services.CreateAsyncScope();
                var approvalStep = source.Steps.Single(s => s.StepId == "managerApproval");
                var echoed = approvalCommandWire.BasicProperties.Headers!
                    .Where(pair => pair.Key == "sf-version" || pair.Key.StartsWith("sf-ex-", StringComparison.Ordinal))
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
                echoed["sf-causation-id"] = approvalCommandWire.BasicProperties.MessageId!;
                echoed["sf-outcome"] = "success";
                var duplicate = new RabbitMQ.Client.BasicProperties
                {
                    ContentType = "application/json", Type = "RequestManagerApprovalCompleted", MessageId = Guid.NewGuid().ToString("N"),
                    CorrelationId = approvalCommandWire.BasicProperties.CorrelationId, Headers = echoed
                };
                var stableSource = (await sourceStore.FindAsync(source.Id))!;
                await wire.BasicPublishAsync("", approvalCommandWire.BasicProperties.ReplyTo!, true, duplicate,
                    JsonSerializer.SerializeToUtf8Bytes(approvalStep.Result!.Value));
                await Until(async () => await wire.MessageCountAsync("procurement.outcomes") == 0);
                await Task.Delay(250);
                Assert.Equal(stableSource.Revision, (await sourceStore.FindAsync(source.Id))!.Revision);
                Assert.Equal(targetCount + 1, (await targetStore.ListAsync()).Count);
                Assert.Equal(request.Identity.InvocationId,
                    (await sourceStore.FindAsync(source.Id))!.Steps.Single(s => s.StepId == "invokeOrder").Invocation!.Identity.InvocationId);
                var wrong = new ProcessInvocationIdentity(request.Identity.InvocationId, "wrong-correlation", request.Identity.Source,
                    source.Id, request.Identity.SourceStep, request.Identity.Target);
                await Assert.ThrowsAsync<InvalidOperationException>(async () => await sourceScope.ServiceProvider.GetRequiredService<ProcessInvocationReceiver>()
                    .ReceiveAsync(new ProcessInvocationTerminalOutcome(new(wrong, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow), target.Id, ExternalProcessStatus.Completed)));
                Assert.Equal(request.Identity.InvocationId, (await sourceStore.FindAsync(source.Id))!.Steps.Single(s => s.StepId == "invokeOrder").Invocation!.Identity.InvocationId);
            }
        }
        finally
        {
            await SampleRabbitMqEndToEndTests.StopWorkersAsync(workers);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task Until(Func<Task<bool>> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline) { if (await predicate()) return; await Task.Delay(100); }
        Assert.Fail("Procurement/Order workflow did not reach its expected state within 30 seconds.");
    }
}
