using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Engine.Messaging;
using SagaFlow.Engine.Coordination;
using SagaFlow.Engine.ProcessDefinition;
using SagaFlow.Procurement;
using SagaFlow.Order.Services;
using SagaFlow.Order.Repositories;

namespace SagaFlow.Samples.Tests;

public sealed class ProcurementDomainTests
{
    private sealed class Bus(bool fail = false) : ISagaMessageBus
    {
        public ConcurrentQueue<SagaCommand> Commands = new();
        public ValueTask PublishOutboxAsync(OutboxMessage message, CancellationToken ct = default) =>
            PublishAsync(message.ToCommand(), ct);
        public ValueTask PublishAsync(SagaCommand command, CancellationToken ct = default)
        { if (fail) throw new IOException("Approval unavailable"); Commands.Enqueue(command); return ValueTask.CompletedTask; }
    }
    private static WebApplicationFactory<ProcurementProgram> Host(Bus bus) => new WebApplicationFactory<ProcurementProgram>().WithWebHostBuilder(builder =>
        builder.ConfigureServices(services => { services.RemoveAll<IHostedService>(); services.AddSingleton<ISagaMessageBus>(bus); }));
    private static void Load(WebApplicationFactory<ProcurementProgram> host) => host.Services.GetRequiredService<ProcessDefinitionRegistry>().RegisterLocalBatch([
        new DefinitionYamlCodec().Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "procurement-process.yaml")))]);

    [Fact]
    public async Task ValidRequestStartsApprovalAndRejectionCompletesLocallyWithoutInvocation()
    {
        var bus = new Bus(); await using var host = Host(bus); using var client = host.CreateClient(); Load(host);
        var response = await client.PostAsJsonAsync("/purchase-requests", new CreatePurchaseRequest(" employee ", "customer", [new("product", 2, 10m)]));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode); var created = (await response.Content.ReadFromJsonAsync<PurchaseCreated>())!;
        Assert.NotEqual(created.PurchaseRequestId, created.SagaInstanceId);
        var command = Assert.Single(bus.Commands); Assert.Equal("RequestManagerApproval", command.Name);
        Assert.Equal(created.PurchaseRequestId, command.Payload.GetProperty("id").GetGuid()); Assert.Equal(2, command.Payload.GetProperty("totalQuantity").GetInt64());
        await using var scope = host.Services.CreateAsyncScope(); var receiver = scope.ServiceProvider.GetRequiredService<OutcomeEventReceiver>();
        var outcome = new SagaOutcomeEvent { Headers = command.Headers, IsSuccess = true, Result = JsonSerializer.SerializeToElement(new { approved = false }) };
        await receiver.ReceiveAsync(outcome);
        await Assert.ThrowsAsync<DuplicateSagaOutcomeException>(() => receiver.ReceiveAsync(outcome));
        var view = (await client.GetFromJsonAsync<PurchaseView>($"/purchase-requests/{created.PurchaseRequestId}"))!;
        Assert.Equal("Rejected", view.Status); Assert.Equal("Completed", view.SagaStatus); Assert.Null(view.InvocationId);
        Assert.Single(bus.Commands); Assert.Equal("employee", view.EmployeeId);
    }
    [Theory]
    [InlineData("", 1, 10)] [InlineData("employee", 0, 10)] [InlineData("employee", 1, 0)]
    public async Task InvalidPurchaseDoesNotStartSaga(string employee, int quantity, decimal price)
    {
        var bus = new Bus(); await using var host = Host(bus); using var client = host.CreateClient(); Load(host);
        var response = await client.PostAsJsonAsync("/purchase-requests", new CreatePurchaseRequest(employee, "customer", [new("product", quantity, price)]));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Empty(bus.Commands);
        Assert.Empty(await host.Services.GetRequiredService<ISagaStateStore>().ListAsync());
    }
    [Fact]
    public async Task PreInvocationDispatchFailurePreservesBusinessRequestAndPendingApprovalCommand()
    {
        await using var host = Host(new Bus(true)); using var client = host.CreateClient(); Load(host);
        var response = await client.PostAsJsonAsync("/purchase-requests", new CreatePurchaseRequest("employee", "customer", [new("product", 1, 10m)]));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("purchaseRequestId").GetGuid();
        var view = (await client.GetFromJsonAsync<PurchaseView>($"/purchase-requests/{id}"))!;
        Assert.Equal("AwaitingApproval", view.Status); Assert.Equal("Running", view.SagaStatus); Assert.Null(view.InvocationId);
        var pending = Assert.Single(await host.Services.GetRequiredService<ISagaStateStore>().ClaimOutboxAsync(
            "inspection", DateTimeOffset.UtcNow.AddMinutes(2), TimeSpan.FromMinutes(1), 1));
        Assert.Equal("RequestManagerApproval", pending.Message.Name);
    }
    [Fact]
    public async Task OrderAdmissionRejectsInvalidBusinessDataAndConcurrentDuplicatesCreateOneOrder()
    {
        var repository = new InMemoryOrderRepository(); var service = new IncomingOrderService(repository, TimeProvider.System);
        var identity = new ProcessInvocationIdentity(Guid.NewGuid().ToString("N"), "source-correlation", new("procurement", "procurement-process", "1.0"),
            "source-saga", "invokeOrder", new("order", "order-process", "1.0"));
        var invalid = new ProcessInvocationRequest(identity, JsonSerializer.SerializeToElement(new { purchaseRequestId = Guid.NewGuid(), order = new { customerId = "", items = Array.Empty<object>() } }));
        Assert.NotNull(await service.ValidateAsync(invalid, default)); Assert.Null(await service.FindAsync(identity.InvocationId, default));
        var purchaseId = Guid.NewGuid();
        var request = new ProcessInvocationRequest(identity, JsonSerializer.SerializeToElement(new { purchaseRequestId = purchaseId, order = new { customerId = "company", items = new[] { new { productId = "product", quantity = 2, unitPrice = 10m } } } }));
        Assert.Null(await service.ValidateAsync(request, default));
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ => await service.InitializeAsync(request, "target-saga", default)));
        Assert.Single(results.Select(r => r.GetProperty("id").GetGuid()).Distinct());
        var link = (await service.FindAsync(identity.InvocationId, default))!;
        Assert.Equal(purchaseId, link.PurchaseRequestId); Assert.NotEqual(purchaseId, link.Order.Id); Assert.Equal(20m, link.Order.Total);
        Assert.Equal("target-saga", link.TargetSagaInstanceId); Assert.Same(link.Order, await repository.FindAsync(link.Order.Id));
    }
}
