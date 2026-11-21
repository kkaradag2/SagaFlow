using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RabbitMQ.Client;

namespace SagaFlow.Samples.Tests;

public sealed class SampleEndToEndTests
{
    [SampleRabbitMqFact]
    public async Task DomainStartersAndParticipantsHandleSuccessDecisionsFailuresAndRestarts()
    {
        await using var sample = new SampleTestEnvironment();
        await sample.StartAsync();
        await VerifyDefinitionsAsync(sample);
        await VerifyOrderAsync(sample);
        await VerifyReturnAsync(sample);
        await VerifyProcurementAsync(sample);
    }

    private static async Task VerifyDefinitionsAsync(SampleTestEnvironment sample)
    {
        var orderGraph = "/sagaflow/api/definition-graph?domain=order&process=order-process&version=1.0";
        var returnGraph = "/sagaflow/api/definition-graph?domain=return&process=return-process&version=1.0";
        await AssertGraphAsync(sample, "Order", orderGraph, ("return", "related"), ("procurement", "related"));
        await AssertGraphAsync(sample, "Return", returnGraph, ("order", "related"));
        await AssertGraphAsync(sample, "Procurement",
            "/sagaflow/api/definition-graph?domain=procurement&process=procurement-process&version=1.0", ("order", "invokes"));

        await sample.StopAsync("Return");
        var offline = await GetAsync(sample, "Order", orderGraph);
        Assert.Equal(JsonValueKind.Null, offline.GetProperty("edges").EnumerateArray().Single(edge =>
            edge.GetProperty("targetDomain").GetString() == "return").GetProperty("target").ValueKind);
        Assert.Single(offline.GetProperty("failures").EnumerateArray());
        sample.StartDomain("Return");
        await sample.EventuallyAsync(async () => (await GetAsync(sample, "Order", orderGraph))
            .GetProperty("failures").GetArrayLength() == 0, "Return was not rediscovered after restart.");

        foreach (var (name, path) in new[] { ("Order", "/orders"), ("Return", "/returns"),
                     ("Procurement", "/purchase-requests") })
        {
            var document = await GetAsync(sample, name, "/swagger/v1/swagger.json");
            Assert.Contains(document.GetProperty("paths").EnumerateObject(), property => property.Name == path);
            Assert.DoesNotContain(document.GetProperty("paths").EnumerateObject(),
                property => property.Name.StartsWith("/sagaflow", StringComparison.Ordinal));
            Assert.Equal(0L, await sample.QueryAsync<long>(name, "SELECT count(*) FROM sagaflow.instances"));
        }
    }

    private static async Task AssertGraphAsync(SampleTestEnvironment sample, string service, string path,
        params (string Target, string Relationship)[] expected)
    {
        var graph = await GetAsync(sample, service, path);
        Assert.Empty(graph.GetProperty("failures").EnumerateArray());
        Assert.Single(graph.GetProperty("nodes").EnumerateArray(), node => Text(node, "origin") == "Local");
        var edges = graph.GetProperty("edges").EnumerateArray().Select(edge =>
        {
            Assert.Equal(JsonValueKind.Object, edge.GetProperty("target").ValueKind);
            Assert.Equal(service.ToLowerInvariant(), Text(edge.GetProperty("source"), "domain"));
            return (Text(edge, "targetDomain")!, Text(edge, "relationship")!);
        }).ToArray();
        Assert.Equal(expected.OrderBy(edge => edge.Target), edges.OrderBy(edge => edge.Item1));
    }

    private static async Task VerifyOrderAsync(SampleTestEnvironment sample)
    {
        using (var invalid = await sample.Http.PostAsJsonAsync(sample.Url("Order", "/orders"),
                   new { customerId = " ", items = Array.Empty<object>() }))
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var completed = await StartOrderAsync(sample, quantity: 1000, sku: "sku-bulk");
        var completedId = completed.GetProperty("orderId").GetGuid();
        await StatusAsync(sample, "Order", SagaPath(completed), "status", "Completed");
        Assert.Equal("Authorized", await OrderRecordAsync(sample, "Payment", "payment.authorizations", completedId));
        Assert.Equal("Reserved", await OrderRecordAsync(sample, "Inventory", "inventory.reservations", completedId));
        Assert.Equal("Created", await OrderRecordAsync(sample, "Shipment", "shipment.shipments", completedId));

        await sample.QueryAsync<long>("Inventory", """
            INSERT INTO inventory.stock (product_id, available_quantity) VALUES ('sku-approval', 2000)
            RETURNING available_quantity
            """);
        var manager = await StartOrderAsync(sample, quantity: 1001, sku: "sku-approval");
        var managerId = manager.GetProperty("orderId").GetGuid();
        var approvalId = await PendingApprovalAsync(sample, managerId);
        await DecisionAsync(sample, "ManagerApproval", $"/approvals/{approvalId}/decision", "reject");
        await StatusAsync(sample, "Order", SagaPath(manager), "status", "Compensated");
        Assert.Equal("Cancelled", await OrderRecordAsync(sample, "Order", "ordering.orders", managerId, "id"));

        var cancelled = await StartOrderAsync(sample, failStep: "notification");
        var cancelledId = cancelled.GetProperty("orderId").GetGuid();
        var saga = await StatusAsync(sample, "Order", SagaPath(cancelled), "status", "Compensated");
        Assert.Equal("Completed", Text(saga.GetProperty("domainRollback"), "status"));
        Assert.Equal("Cancelled", await OrderRecordAsync(sample, "Order", "ordering.orders", cancelledId, "id"));
        Assert.Equal("Cancelled", await OrderRecordAsync(sample, "Payment", "payment.authorizations", cancelledId));
        Assert.Equal("Released", await OrderRecordAsync(sample, "Inventory", "inventory.reservations", cancelledId));
        Assert.Equal("Cancelled", await OrderRecordAsync(sample, "Shipment", "shipment.shipments", cancelledId));
    }

    private static async Task VerifyReturnAsync(SampleTestEnvironment sample)
    {
        var paymentAudit = await WatchAsync(sample, "payment");
        var inventoryAudit = await WatchAsync(sample, "inventory");
        var startingStock = await StockAsync(sample);
        var accepted = await StartReturnAsync(sample);
        var returnId = accepted.GetProperty("returnId").GetGuid();
        var path = $"/returns/{returnId}";
        var pending = await StatusAsync(sample, "Warehouse", $"/inspections/{returnId}", "status", "Pending");
        Assert.False(pending.TryGetProperty("headers", out _));
        Assert.False(pending.TryGetProperty("replyTo", out _));
        await sample.RestartAsync("Warehouse");
        var recovered = await StatusAsync(sample, "Warehouse", $"/inspections/{returnId}", "status", "Pending");
        Assert.Equal(Text(pending, "inspectionId"), Text(recovered, "inspectionId"));
        await DecisionAsync(sample, "Warehouse", $"/inspections/{returnId}/decision", "accept");
        var result = await StatusAsync(sample, "Return", path, "businessStatus", "Accepted");
        Assert.Equal("Completed", Text(result, "sagaStatus"));
        Assert.Equal(49.75m, await sample.QueryAsync<decimal>("Payment",
            "SELECT amount FROM payment.return_refunds WHERE return_id=$1", returnId));
        Assert.Equal(startingStock + 2, await StockAsync(sample));

        // Replay the actual forward commands with their original execution headers.
        await ReplayAsync(sample, paymentAudit, "payment", "RefundReturnPayment");
        await ReplayAsync(sample, inventoryAudit, "inventory", "RestockReturn");
        await sample.EventuallyAsync(async () =>
            (await sample.Channel.QueueDeclarePassiveAsync("payment")).MessageCount == 0 &&
            (await sample.Channel.QueueDeclarePassiveAsync("inventory")).MessageCount == 0);
        using (var conflict = await sample.Http.PostAsJsonAsync(sample.Url("Warehouse", $"/inspections/{returnId}/decision"),
                   new { decision = "reject" }))
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(1L, await sample.QueryAsync<long>("Payment", "SELECT count(*) FROM payment.return_refunds WHERE return_id=$1", returnId));
        Assert.Equal(1L, await sample.QueryAsync<long>("Inventory", "SELECT count(*) FROM inventory.return_restocks WHERE return_id=$1", returnId));
        Assert.Equal(startingStock + 2, await StockAsync(sample));

        foreach (var (failStep, decision, expected, refund, stock) in new[]
        {
            ((string?)null, "reject", "Rejected", 0L, 0L),
            ("payment", "accept", "Failed", 0L, 0L),
            ("inventory", "accept", "ManualReview", 1L, 0L),
            ("acceptedNotification", "accept", "ManualReview", 1L, 1L),
            ("rejectedNotification", "reject", "Failed", 0L, 0L)
        })
        {
            var created = await StartReturnAsync(sample, failStep);
            var id = created.GetProperty("returnId").GetGuid();
            await StatusAsync(sample, "Warehouse", $"/inspections/{id}", "status", "Pending");
            await DecisionAsync(sample, "Warehouse", $"/inspections/{id}/decision", decision);
            await StatusAsync(sample, "Return", $"/returns/{id}", "businessStatus", expected);
            Assert.Equal(refund, await sample.QueryAsync<long>("Payment", "SELECT count(*) FROM payment.return_refunds WHERE return_id=$1", id));
            Assert.Equal(stock, await sample.QueryAsync<long>("Inventory", "SELECT count(*) FROM inventory.return_restocks WHERE return_id=$1", id));
        }

        var failed = await StartReturnAsync(sample, "warehouse");
        var failedId = failed.GetProperty("returnId").GetGuid();
        await StatusAsync(sample, "Return", $"/returns/{failedId}", "businessStatus", "Failed");
        Assert.Equal(0L, await sample.QueryAsync<long>("Warehouse", "SELECT count(*) FROM inspection.requests WHERE return_id=$1", failedId));
        Assert.Equal(startingStock + 4, await StockAsync(sample));
    }

    private static async Task VerifyProcurementAsync(SampleTestEnvironment sample)
    {
        foreach (var (decision, failStep, status, orderStatus, orderRecord) in new[]
        {
            ("reject", (string?)null, "Rejected", (string?)null, (string?)null),
            ("approve", null, "Fulfilled", "Completed", "Created"),
            ("approve", "notification", "Failed", "Compensated", "Cancelled")
        })
        {
            var body = new { employeeId = "sample-test", customerId = "sample-customer",
                items = new[] { new { productId = "sku-1", quantity = 1, unitPrice = 25m } }, failStep };
            var created = await PostCreatedAsync(sample, "Procurement", "/purchase-requests", body);
            var purchase = created.GetProperty("purchaseRequestId").GetGuid();
            var path = $"/purchase-requests/{purchase}";
            await StatusAsync(sample, "Procurement", path, "status", "AwaitingApproval");
            if (decision == "reject") await sample.RestartAsync("ManagerApproval");
            var approvalId = await PendingApprovalAsync(sample, purchase);
            await DecisionAsync(sample, "ManagerApproval", $"/approvals/{approvalId}/decision", decision);
            var result = await StatusAsync(sample, "Procurement", path, "status", status);
            Assert.Equal(orderStatus, Text(result, "lastKnownOrderStatus"));
            if (decision == "reject")
            {
                Assert.Null(Text(result, "invocationId"));
                Assert.Equal(0L, await sample.QueryAsync<long>("Order",
                    "SELECT count(*) FROM ordering.incoming_orders WHERE purchase_request_id=$1", purchase));
                continue;
            }
            var invocationId = Guid.Parse(Text(result, "invocationId")!);
            var target = Text(result, "orderSagaInstanceId")!;
            var orderId = await sample.QueryAsync<Guid>("Order", """
                SELECT order_id FROM ordering.incoming_orders
                WHERE purchase_request_id=$1 AND invocation_id=$2 AND saga_instance_id=$3
                """, purchase, invocationId, Guid.Parse(target));
            Assert.Equal(orderRecord, await OrderRecordAsync(sample, "Order", "ordering.orders", orderId, "id"));
            var targetView = await GetAsync(sample, "Order", $"/sagaflow/api/instances/{target}");
            Assert.Equal("External", Text(targetView, "origin"));
            Assert.Equal(Text(result, "invocationId"), Text(targetView.GetProperty("incomingInvocation"), "invocationId"));
            await sample.RestartAsync("Procurement");
            await StatusAsync(sample, "Procurement", path, "status", status);
        }
    }

    private static async Task<string> PendingApprovalAsync(SampleTestEnvironment sample, Guid purchase)
    {
        string? id = null;
        await sample.EventuallyAsync(async () =>
        {
            var pending = await GetAsync(sample, "ManagerApproval", "/approvals/pending");
            id = pending.EnumerateArray().Where(item => item.GetProperty("orderId").GetGuid() == purchase)
                .Select(item => Text(item, "approvalId")).SingleOrDefault();
            return id is not null;
        });
        return id!;
    }

    private static async Task DecisionAsync(SampleTestEnvironment sample, string service, string path, string decision)
    {
        using var response = await sample.Http.PostAsJsonAsync(sample.Url(service, path), new { decision });
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted,
            $"{service} decision failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task<string> WatchAsync(SampleTestEnvironment sample, string routingKey)
    {
        var audit = await sample.Channel.QueueDeclareAsync();
        await sample.Channel.QueueBindAsync(audit.QueueName, "sagaflow.commands", routingKey);
        return audit.QueueName;
    }

    private static async Task ReplayAsync(SampleTestEnvironment sample, string audit, string queue, string command)
    {
        var delivery = await sample.Channel.BasicGetAsync(audit, autoAck: true);
        Assert.NotNull(delivery);
        Assert.Equal(command, delivery.BasicProperties.Type);
        await sample.Channel.BasicPublishAsync("", queue, mandatory: true,
            new BasicProperties(delivery.BasicProperties), delivery.Body);
    }

    private static Task<JsonElement> StartOrderAsync(SampleTestEnvironment sample, string? failStep = null,
        int quantity = 1, string sku = "sku-1") => PostCreatedAsync(sample, "Order", "/orders",
        new { customerId = "sample-customer", items = new[] { new { productId = sku, quantity, unitPrice = 12.50m } }, failStep });

    private static Task<JsonElement> StartReturnAsync(SampleTestEnvironment sample, string? failStep = null)
        => PostCreatedAsync(sample, "Return", "/returns", new { orderId = Guid.NewGuid(), customerId = "sample-customer",
            amount = 49.75m, items = new[] { new { productId = "sku-1", quantity = 2 } }, failStep });

    private static string SagaPath(JsonElement created) => $"/sagaflow/api/instances/{created.GetProperty("sagaInstanceId").GetGuid():N}";
    private static string? Text(JsonElement value, string property) => value.GetProperty(property).GetString();
    private static Task<long> StockAsync(SampleTestEnvironment sample) => sample.QueryAsync<long>("Inventory",
        "SELECT available_quantity FROM inventory.stock WHERE product_id='sku-1'");
    private static Task<string> OrderRecordAsync(SampleTestEnvironment sample, string service, string table, Guid id,
        string column = "order_id") => sample.QueryAsync<string>(service, $"SELECT status FROM {table} WHERE {column}=$1", id);

    private static async Task<JsonElement> PostCreatedAsync(SampleTestEnvironment sample, string name, string path, object input)
    {
        using var response = await sample.Http.PostAsJsonAsync(sample.Url(name, path), input);
        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"{name} POST returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<JsonElement> GetAsync(SampleTestEnvironment sample, string service, string path)
        => sample.Http.GetFromJsonAsync<JsonElement>(sample.Url(service, path));

    private static async Task<JsonElement> StatusAsync(SampleTestEnvironment sample, string service,
        string path, string field, string status)
    {
        JsonElement result = default;
        await sample.EventuallyAsync(async () =>
        {
            using var response = await sample.Http.GetAsync(sample.Url(service, path));
            if (!response.IsSuccessStatusCode) return false;
            result = await response.Content.ReadFromJsonAsync<JsonElement>();
            return Text(result, field) == status;
        }, $"{service} {path} expected {field}={status}.");
        return result;
    }
}
