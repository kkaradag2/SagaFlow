using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RabbitMQ.Client;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.ManagerApproval;
using SagaFlow.Order.Contracts.Responses;
using SagaFlow.Order.Domain;
using SagaFlow.Order.Services;
using SagaFlow.Procurement;
using SagaFlow.Returns;
using SagaFlow.Storage.InMemory;
using SagaFlow.Storage.InMemory.DependencyInjection;
using SagaFlow.Storage.PostgreSQL;
using SagaFlow.Storage.PostgreSQL.DependencyInjection;
using SagaFlow.Hosting.DependencyInjection;
using Xunit.Abstractions;

namespace SagaFlow.Samples.Tests;

public sealed class SamplePostgreSqlRabbitMqFactAttribute : FactAttribute
{
    public SamplePostgreSqlRabbitMqFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SAGAFLOW_SAMPLE_RABBITMQ_INTEGRATION") != "true" ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SAGAFLOW_SAMPLE_POSTGRESQL_TEST_PORT")))
            Skip = "Enable isolated sample RabbitMQ tests and set SAGAFLOW_SAMPLE_POSTGRESQL_TEST_PORT to a dedicated test server initialized with samples/postgres/init-domains.sql.";
    }
}

[Collection("sample-rabbitmq-e2e")]
public sealed class SamplePostgreSqlTests(ITestOutputHelper output)
{
    [Fact]
    public async Task InMemoryRemainsAvailableInAllThreeSamples()
    {
        await AssertInMemoryAsync<global::Program>();
        await AssertInMemoryAsync<ReturnProgram>();
        await AssertInMemoryAsync<ProcurementProgram>();
    }

    [Theory]
    [InlineData(null, "Set the PostgreSQL ConnectionString")]
    [InlineData(" ", "Set the PostgreSQL ConnectionString")]
    [InlineData("invalid", "valid Npgsql connection string")]
    [InlineData("Host=localhost;Username=sagaflow_order", "Specify Host and Database")]
    public async Task InvalidStorageConfigurationFailsBeforeServingRequests(string? connection, string expected)
    {
        await using var host = SampleDomainProcess.Start("Order", SampleDomainProcess.AllocateEndpoints(1)[0],
            StorageSettings(connection));
        Assert.Contains(expected, await host.WaitForStartupFailureAsync());
    }

    [Fact]
    public async Task PostgreSqlUnavailabilityFailsStartupWithoutFallback()
    {
        var port = new Uri(SampleDomainProcess.AllocateEndpoints(1)[0]).Port;
        await using var host = SampleDomainProcess.Start("Order", SampleDomainProcess.AllocateEndpoints(1)[0],
            StorageSettings($"Host=127.0.0.1;Port={port};Database=sagaflow_order;Username=sagaflow_order;Timeout=1"));
        var diagnostics = await host.WaitForStartupFailureAsync();
        Assert.Contains("Cannot initialize SagaFlow PostgreSQL storage.", diagnostics);
        Assert.Contains("Check PostgreSQL readiness, credentials, and schema version", diagnostics);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConflictingFluentStorageSelectionsIdentifyBothProviders(bool inMemoryFirst)
    {
        var services = new ServiceCollection();
        var error = Assert.Throws<InvalidOperationException>(() => services.AddSagaFlow(options =>
        {
            void SelectPostgreSql() => options.Storage.UsePostgreSql(storage =>
                storage.ConnectionString = "Host=localhost;Database=sagaflow_order");
            if (inMemoryFirst) options.Storage.UseInMemory();
            SelectPostgreSql();
            if (!inMemoryFirst) options.Storage.UseInMemory();
        }));
        Assert.Contains("Choose exactly one provider in options.Storage", error.Message);
        Assert.Contains(nameof(InMemoryStorageServiceCollectionExtensions.UseInMemory), error.Message);
        Assert.Contains(nameof(PostgreSqlStorageServiceCollectionExtensions.UsePostgreSql), error.Message);
        Assert.DoesNotContain(services, service => service.ServiceType == typeof(ISagaStateStore));
    }

    [SamplePostgreSqlRabbitMqFact]
    public async Task ProcurementAndOrderRecoverInvocationOutboxesAcrossProcessCrashes()
    {
        SampleRabbitMqTestIsolation.RequireVirtualHost();
        const string exchange = "sagaflow.process-invocations";
        var endpoints = SampleDomainProcess.AllocateEndpoints(3);
        var orders = Domain.Create("Order", endpoints[0], endpoints[1]);
        var procurement = Domain.Create("Procurement", endpoints[2]);
        var root = Directory.CreateTempSubdirectory("sagaflow-invocation-outbox-");
        var workers = new List<System.Diagnostics.Process>();
        try
        {
            await UseTestDefinitionAsync(root, procurement, "procurement-process.yaml", "https://localhost:50823", endpoints[1]);
            await using var approvals = new WebApplicationFactory<ManagerApprovalProgram>();
            using var approvalClient = approvals.CreateClient();
            await using var broker = await SampleRabbitMqEndToEndTests.CreateConnectionAsync();
            await using var channel = await broker.CreateChannelAsync();
            workers = SampleRabbitMqEndToEndTests.StartWorkers();
            var requestQueue = InvocationQueue(new("order", "order-process", "1.0"), "requests");
            var outcomeQueue = InvocationQueue(new("procurement", "procurement-process", "1.0"), "outcomes");
            string sourceId;
            int firstSourcePid;
            int firstTargetPid;
            OutboxMessage request;
            SagaInstance target;
            OutboxMessage acceptance;
            OutboxMessage terminal;
            SampleDomainProcess? resumedSource = null;
            try
            {
                await using (var targetHost = orders.Start())
                {
                    firstTargetPid = targetHost.Id;
                    await targetHost.WaitForStartupAsync();
                    await channel.QueueUnbindAsync(requestQueue, exchange, requestQueue);
                    await using (var sourceHost = procurement.Start())
                    {
                        firstSourcePid = sourceHost.Id;
                        await sourceHost.WaitForStartupAsync();
                        await SampleRabbitMqEndToEndTests.WaitForConsumersAsync(broker, includeApproval: true);
                        var purchase = await PostAsync<PurchaseCreated>(sourceHost.Client, "/purchase-requests",
                            new CreatePurchaseRequest("outbox-employee", "outbox-customer", [new("sku", 2, 12.50m)]));
                        sourceId = purchase.SagaInstanceId.ToString("N");
                        await AwaitBusinessRecordAsync(approvalClient, $"/approvals/{purchase.PurchaseRequestId}");
                        using var approved = await approvalClient.PostAsJsonAsync($"/approvals/{purchase.PurchaseRequestId}/decision", new { decision = "approve" });
                        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
                        await UntilAsync(async () => (await InvocationRowsAsync(procurement, sourceId)).SingleOrDefault() is
                            { Message: not null, Published: false, Released: true }, "Procurement did not retain its unroutable request.");
                        var row = Assert.Single(await InvocationRowsAsync(procurement, sourceId));
                        Assert.False(row.Published);
                        request = row.Message;
                        var state = (await procurement.Store.FindAsync(sourceId))!;
                        var step = state.Steps.Single(x => x.StepId == "invokeOrder");
                        Assert.Equal(ExternalProcessStatus.Requested, step.InvocationProgress!.Status);
                        Assert.Equal(step.Invocation!.Message, request.Invocation!.Message);
                        Assert.Equal(requestQueue, request.Destination);
                        Assert.Equal(outcomeQueue, request.ReplyDestination);
                        Assert.Null(await orders.Store.FindIncomingDecisionAsync(step.Invocation.Identity.InvocationId));
                        Assert.Equal(0u, await channel.MessageCountAsync(requestQueue));
                    } // Kill the original OS process while its invocation cannot be routed.

                    resumedSource = procurement.Start();
                    Assert.NotEqual(firstSourcePid, resumedSource.Id);
                    await resumedSource.WaitForStartupAsync();
                    await channel.QueueUnbindAsync(outcomeQueue, exchange, outcomeQueue);
                    await channel.QueueBindAsync(requestQueue, exchange, requestQueue);
                    var identity = request.Invocation!.Message.Identity;
                    await UntilAsync(async () => (await orders.Store.ListAsync()).Any(x =>
                        x.IncomingInvocation?.Identity == identity && x.Status == SagaStatus.Completed), "Order did not complete the recovered invocation.");
                    target = Assert.Single(await orders.Store.ListAsync(), x => x.IncomingInvocation?.Identity == identity);
                    await UntilAsync(async () => (await InvocationRowsAsync(orders, target.Id)) is { Count: 2 } replies &&
                        replies.All(x => x.Released && !x.Published), "Order did not retain both unroutable responses.");
                    Assert.Equal(request.Invocation.Message, target.IncomingInvocation!.RequestMessage);
                    var admission = (await orders.Store.FindIncomingDecisionAsync(identity.InvocationId))!;
                    Assert.Null(admission.RejectionReason);
                    Assert.Equal(request.Invocation.Message, admission.Request.Message);
                    var rows = await InvocationRowsAsync(orders, target.Id);
                    Assert.Equal(2, rows.Count);
                    Assert.All(rows, row => Assert.False(row.Published));
                    acceptance = Assert.Single(rows, row => row.Message.Name == nameof(ProcessInvocationAccepted)).Message;
                    terminal = Assert.Single(rows, row => row.Message.Name == nameof(ProcessInvocationTerminalOutcome)).Message;
                    Assert.Equal(admission.Reply, acceptance.Invocation!.Message);
                    Assert.Equal(target.InvocationTerminalOutcome, terminal.ToInvocation());
                    Assert.Equal(target.Id, acceptance.Invocation.TargetSagaInstanceId);
                } // Kill Order after the terminal commit, while both responses remain unroutable.

                await channel.QueueBindAsync(outcomeQueue, exchange, outcomeQueue);
                await using var resumedTarget = orders.Start();
                Assert.NotEqual(firstTargetPid, resumedTarget.Id);
                await resumedTarget.WaitForStartupAsync();
                var completed = await CompletedAsync(procurement.Store, sourceId);
                var finished = completed.Steps.Single(x => x.StepId == "invokeOrder");
                Assert.Equal(ExternalProcessStatus.Completed, finished.InvocationProgress!.Status);
                Assert.Equal(target.Id, finished.InvocationProgress.TargetSagaInstanceId);
                Assert.Equal(request.Invocation!.Message, finished.Invocation!.Message);
                Assert.Contains(finished.ObservedMessages, x => x.MessageId == acceptance.MessageId);
                Assert.Contains(finished.ObservedMessages, x => x.MessageId == terminal.MessageId);
                await UntilAsync(async () => (await InvocationRowsAsync(procurement, sourceId)).All(x => x.Published) &&
                    (await InvocationRowsAsync(orders, target.Id)).All(x => x.Published), "Invocation confirmations were not recorded.");
                Assert.Equal(request.MessageId, Assert.Single(await InvocationRowsAsync(procurement, sourceId)).Message.MessageId);
                Assert.Equal(2, (await InvocationRowsAsync(orders, target.Id)).Count);
                Assert.Single(await orders.Store.ListAsync(), x => x.IncomingInvocation?.Identity == request.Invocation.Message.Identity);
            }
            finally { if (resumedSource is not null) await resumedSource.DisposeAsync(); }
        }
        finally
        {
            await SampleRabbitMqEndToEndTests.StopWorkersAsync(workers);
            root.Delete(recursive: true);
        }
    }

    private static string InvocationQueue(ProcessIdentity identity, string kind) =>
        $"sagaflow.invocations.{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(identity)))}.{kind}";

    private static async Task<List<(OutboxMessage Message, bool Published, bool Released)>> InvocationRowsAsync(Domain domain, string sagaId)
    {
        await using var connection = new NpgsqlConnection(domain.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT document::text, published_at IS NOT NULL, attempts > 0 AND owner IS NULL FROM sagaflow.outbox
            WHERE saga_instance_id = @id AND document->>'Invocation' IS NOT NULL
            """, connection);
        command.Parameters.AddWithValue("id", sagaId);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<(OutboxMessage, bool, bool)>();
        while (await reader.ReadAsync())
            rows.Add((JsonSerializer.Deserialize<OutboxMessage>(reader.GetString(0))!, reader.GetBoolean(1), reader.GetBoolean(2)));
        return rows;
    }

    [SamplePostgreSqlRabbitMqFact]
    public async Task ThreeDomainProcessesPersistAndRediscoverTheirStateAfterRestart()
    {
        SampleRabbitMqTestIsolation.RequireVirtualHost();
        var endpoints = SampleDomainProcess.AllocateEndpoints(5);
        var root = Directory.CreateTempSubdirectory("sagaflow-pbi04-");
        var workers = new List<System.Diagnostics.Process>();
        try
        {
            var returns = Domain.Create("Return", endpoints[0], endpoints[1]);
            var orders = Domain.Create("Order", endpoints[2], endpoints[3]);
            var procurement = Domain.Create("Procurement", endpoints[4]);
            var foreignConnection = new NpgsqlConnectionStringBuilder(orders.ConnectionString)
            {
                Database = new NpgsqlConnectionStringBuilder(returns.ConnectionString).Database
            };
            await using (var deniedHost = SampleDomainProcess.Start("Order", endpoints[2], StorageSettings(foreignConnection.ConnectionString)))
            {
                var diagnostics = await deniedHost.WaitForStartupFailureAsync();
                Assert.Contains("Cannot initialize SagaFlow PostgreSQL storage.", diagnostics);
                Assert.Contains(PostgresErrorCodes.InsufficientPrivilege, diagnostics);
            }
            await UseTestDefinitionAsync(root, orders, "order-process.yaml", "https://localhost:65305", endpoints[1]);
            await UseTestDefinitionAsync(root, procurement, "procurement-process.yaml", "https://localhost:50823", endpoints[3]);

            var before = new List<(Domain Owner, SagaInstance Snapshot, string Detail, int ProcessId)>();
            await using var approvals = new WebApplicationFactory<ManagerApprovalProgram>();
            await using var warehouse = new WebApplicationFactory<WarehouseProgram>();
            using var approvalClient = approvals.CreateClient();
            using var warehouseClient = warehouse.CreateClient();
            await using var broker = await SampleRabbitMqEndToEndTests.CreateConnectionAsync();
            workers = SampleRabbitMqEndToEndTests.StartWorkers();

            await using (var returnHost = returns.Start())
            await using (var orderHost = orders.Start())
            await using (var procurementHost = procurement.Start())
            {
                await Task.WhenAll(returnHost.WaitForStartupAsync(), orderHost.WaitForStartupAsync(), procurementHost.WaitForStartupAsync());
                Assert.Equal(3, new[] { returnHost.Id, orderHost.Id, procurementHost.Id }.Distinct().Count());
                await SampleRabbitMqEndToEndTests.WaitForConsumersAsync(broker, includeApproval: true);
                await UntilAsync(async () =>
                {
                    await using var channel = await broker.CreateChannelAsync();
                    try { return await channel.ConsumerCountAsync("warehouse") > 0; }
                    catch (RabbitMQ.Client.Exceptions.OperationInterruptedException) { return false; }
                }, "Warehouse consumer did not become ready.");

                await AssertGraphAsync(orderHost.Client, "order", ["order", "return"], "related");
                await AssertGraphAsync(procurementHost.Client, "procurement", ["procurement", "order", "return"], "invokes", "related");

                var created = await PostAsync<SuccessResponse<CreateOrderResult>>(orderHost.Client, "/orders",
                    new CreateOrderRequest("pbi04-customer", [new("sku", 2, 12.50m)]));
                var localOrder = await CompletedAsync(orders.Store, created.Data.SagaInstanceId.ToString("N"));
                Assert.Null(localOrder.IncomingInvocation);
                Assert.All(localOrder.Steps.Where(x => x.ExecutionOrder.HasValue), step =>
                    Assert.Equal(localOrder.CorrelationId, step.Execution!.CorrelationId));
                await RecordAsync(orders, orderHost, localOrder);

                var returned = await PostAsync<ReturnCreated>(returnHost.Client, "/returns",
                    new CreateReturnRequest(created.Data.OrderId, "pbi04-customer", 25m, [new("sku", 2)]));
                await AwaitBusinessRecordAsync(warehouseClient, $"/inspections/{returned.ReturnId}");
                using var accepted = await warehouseClient.PostAsJsonAsync($"/inspections/{returned.ReturnId}/decision", new { decision = "accept" });
                Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
                var localReturn = await CompletedAsync(returns.Store, returned.SagaInstanceId.ToString("N"));
                Assert.Null(localReturn.IncomingInvocation);
                Assert.Equal(new[] { "warehouse", "payment", "inventory", "acceptedNotification" },
                    localReturn.Steps.Where(x => x.ExecutionOrder.HasValue).OrderBy(x => x.ExecutionOrder).Select(x => x.StepId));
                await RecordAsync(returns, returnHost, localReturn);

                var purchase = await PostAsync<PurchaseCreated>(procurementHost.Client, "/purchase-requests",
                    new CreatePurchaseRequest("pbi04-employee", "pbi04-customer", [new("sku", 2, 12.50m)]));
                await AwaitBusinessRecordAsync(approvalClient, $"/approvals/{purchase.PurchaseRequestId}");
                using var approved = await approvalClient.PostAsJsonAsync($"/approvals/{purchase.PurchaseRequestId}/decision", new { decision = "approve" });
                Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
                var source = await CompletedAsync(procurement.Store, purchase.SagaInstanceId.ToString("N"));
                var step = source.Steps.Single(x => x.StepId == "invokeOrder");
                Assert.Equal(ExternalProcessStatus.Completed, step.InvocationProgress!.Status);
                var request = Assert.IsType<ProcessInvocationRequest>(step.Invocation);
                var target = await CompletedAsync(orders.Store, step.InvocationProgress.TargetSagaInstanceId!);
                Assert.Equal(request.Identity, target.IncomingInvocation!.Identity);
                Assert.Equal(source.Id, request.Identity.SourceSagaInstanceId);
                Assert.Equal(source.CorrelationId, request.Identity.CorrelationId);
                Assert.NotEqual(source.CorrelationId, target.CorrelationId);
                Assert.NotEqual(source.Id, target.Id);
                var admission = (await orders.Store.FindIncomingDecisionAsync(request.Identity.InvocationId))!;
                Assert.Null(admission.RejectionReason);
                Assert.Equal(request.Identity, admission.Request.Identity);
                Assert.Null(await procurement.Store.FindIncomingDecisionAsync(request.Identity.InvocationId));
                Assert.Null(await returns.Store.FindIncomingDecisionAsync(request.Identity.InvocationId));
                var link = await orderHost.Client.GetFromJsonAsync<IncomingOrderLink>($"/orders/invocations/{request.Identity.InvocationId}");
                Assert.Equal(purchase.PurchaseRequestId, link!.PurchaseRequestId);
                Assert.Equal(target.Id, link.TargetSagaInstanceId);
                Assert.Equal(link.Order.Id, target.Context.GetProperty("id").GetGuid());
                await RecordAsync(procurement, procurementHost, source);
                await RecordAsync(orders, orderHost, target);
            }

            await using var restartedReturn = returns.Start();
            await using var restartedOrder = orders.Start();
            await using var restartedProcurement = procurement.Start();
            await Task.WhenAll(restartedReturn.WaitForStartupAsync(), restartedOrder.WaitForStartupAsync(), restartedProcurement.WaitForStartupAsync());
            var restarted = new Dictionary<Domain, SampleDomainProcess>
            {
                [returns] = restartedReturn, [orders] = restartedOrder, [procurement] = restartedProcurement
            };
            foreach (var (owner, snapshot, detail, previousProcess) in before)
            {
                var current = restarted[owner];
                Assert.NotEqual(previousProcess, current.Id);
                Assert.Equal(detail, await current.Client.GetStringAsync($"/sagaflow/api/instances/{snapshot.Id}"));
                var loaded = (await new PostgreSqlSagaStateStore(owner.ConnectionString).FindAsync(snapshot.Id))!;
                Assert.Equal(JsonSerializer.Serialize(snapshot), JsonSerializer.Serialize(loaded));
                foreach (var other in restarted.Keys.Where(other => other != owner))
                    Assert.Null(await other.Store.FindAsync(snapshot.Id));
                output.WriteLine($"{owner.Name}: {snapshot.Id} revision={loaded.Revision} status={loaded.Status} correlation={loaded.CorrelationId} " +
                    $"steps={string.Join(',', loaded.Steps.Select(x => $"{x.StepId}:{x.Status}"))} " +
                    $"invocation={loaded.IncomingInvocation?.Identity.InvocationId ?? loaded.Steps.FirstOrDefault(x => x.Invocation is not null)?.Invocation?.Identity.InvocationId ?? "none"} " +
                    $"pid={previousProcess}->{current.Id}");
            }
            foreach (var owner in restarted.Keys)
            {
                foreach (var other in restarted.Keys.Where(other => other != owner))
                {
                    var foreign = new NpgsqlConnectionStringBuilder(owner.ConnectionString)
                    {
                        Database = new NpgsqlConnectionStringBuilder(other.ConnectionString).Database
                    };
                    await using var connection = new NpgsqlConnection(foreign.ConnectionString);
                    var denied = await Assert.ThrowsAsync<PostgresException>(() => connection.OpenAsync());
                    Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
                }
            }
            var returnId = before.Single(x => x.Owner == returns).Snapshot.Context.GetProperty("returnId").GetGuid();
            using var missingReturn = await restartedReturn.Client.GetAsync($"/returns/{returnId}");
            Assert.Equal(HttpStatusCode.NotFound, missingReturn.StatusCode);

            async Task RecordAsync(Domain owner, SampleDomainProcess host, SagaInstance state) =>
                before.Add((owner, state, await host.Client.GetStringAsync($"/sagaflow/api/instances/{state.Id}"), host.Id));
        }
        finally
        {
            await SampleRabbitMqEndToEndTests.StopWorkersAsync(workers);
            root.Delete(recursive: true);
        }
    }

    private static Dictionary<string, string?> StorageSettings(string? connection) => new()
    {
        // Explicitly override the sample template when testing missing connection settings.
        ["ConnectionStrings__SagaFlow"] = connection ?? string.Empty,
        ["RabbitMq__Enabled"] = "false"
    };

    private static async Task AssertInMemoryAsync<T>() where T : class
    {
        await using var factory = new WebApplicationFactory<T>().WithWebHostBuilder(builder => builder
            .UseSetting("RabbitMq:Enabled", "false"));
        using var client = factory.CreateClient();
        Assert.IsType<InMemorySagaStateStore>(factory.Services.GetRequiredService<ISagaStateStore>());
        Assert.Equal("[]", (await client.GetStringAsync("/sagaflow/api/instances")).Trim());
    }

    private static async Task UseTestDefinitionAsync(DirectoryInfo root, Domain domain, string file, string original, string endpoint)
    {
        var directory = root.CreateSubdirectory(domain.Name);
        var yaml = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, file));
        Assert.Contains(original, yaml);
        await File.WriteAllTextAsync(Path.Combine(directory.FullName, file), yaml.Replace(original, endpoint, StringComparison.Ordinal));
        domain.Settings["SagaFlow__DefinitionPath"] = Path.Combine(directory.FullName, file);
    }

    private static async Task AssertGraphAsync(HttpClient client, string domain, string[] expectedDomains, params string[] edges)
    {
        using var graph = JsonDocument.Parse(await client.GetStringAsync($"/sagaflow/api/definition-graph?domain={domain}&process={domain}-process&version=1.0"));
        var nodes = graph.RootElement.GetProperty("nodes").EnumerateArray().ToArray();
        Assert.Equal(expectedDomains.Order(), nodes.Select(x => x.GetProperty("domain").GetString()).Order());
        Assert.Equal("Local", nodes.Single(x => x.GetProperty("domain").GetString() == domain).GetProperty("origin").GetString());
        Assert.All(nodes.Where(x => x.GetProperty("domain").GetString() != domain),
            node => Assert.Equal("Remote", node.GetProperty("origin").GetString()));
        Assert.Equal(edges.Order(), graph.RootElement.GetProperty("edges").EnumerateArray().Select(x => x.GetProperty("relationship").GetString()).Order());
        Assert.Empty(graph.RootElement.GetProperty("failures").EnumerateArray());
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(path, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"POST {path}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static Task AwaitBusinessRecordAsync(HttpClient client, string path) => UntilAsync(async () =>
    {
        using var response = await client.GetAsync(path);
        return response.StatusCode == HttpStatusCode.OK;
    }, $"Business record {path} did not appear.");

    private static async Task<SagaInstance> CompletedAsync(ISagaStateStore store, string id)
    {
        SagaInstance? state = null;
        await UntilAsync(async () =>
        {
            state = await store.FindAsync(id);
            return state?.Status is SagaStatus.Completed or SagaStatus.Compensated or SagaStatus.Failed;
        }, $"Saga {id} did not reach a terminal state.");
        Assert.Equal(SagaStatus.Completed, state!.Status);
        return state;
    }

    private static async Task UntilAsync(Func<Task<bool>> condition, string message)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        Assert.Fail(message);
    }

    private sealed class Domain(string name, string endpoint, string connectionString, Dictionary<string, string?> settings)
    {
        internal string Name { get; } = name;
        internal string ConnectionString { get; } = connectionString;
        internal Dictionary<string, string?> Settings { get; } = settings;
        internal ISagaStateStore Store { get; } = new PostgreSqlSagaStateStore(connectionString);
        internal SampleDomainProcess Start() => SampleDomainProcess.Start(Name, endpoint, Settings);

        internal static Domain Create(string name, string endpoint, string? grpcEndpoint = null)
        {
            var database = "sagaflow_" + name.ToLowerInvariant();
            var connection = new NpgsqlConnectionStringBuilder
            {
                Host = "127.0.0.1", Port = int.Parse(Environment.GetEnvironmentVariable("SAGAFLOW_SAMPLE_POSTGRESQL_TEST_PORT")!),
                Database = database, Username = database, Pooling = false,
                Password = Environment.GetEnvironmentVariable($"SAGAFLOW_POSTGRES_{name.ToUpperInvariant()}_PASSWORD")
                    ?? $"sagaflow-{name.ToLowerInvariant()}-dev"
            }.ConnectionString;
            var config = StorageSettings(connection);
            config["RabbitMq__Enabled"] = "true";
            config["RabbitMq__EnableProcessInvocations"] = (name != "Return").ToString();
            config["SagaFlow__Publication__Enabled"] = "true";
            config["SagaFlow__Publication__IncludeBigPictureSources"] = "true";
            if (grpcEndpoint is not null)
            {
                config["Kestrel__Endpoints__Grpc__Url"] = grpcEndpoint;
                config["Kestrel__Endpoints__Grpc__Protocols"] = "Http2";
            }
            return new Domain(name, endpoint, connection, config);
        }
    }
}
