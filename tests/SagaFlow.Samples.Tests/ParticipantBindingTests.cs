using System.Text.Json;
using System.Text.Json.Nodes;
using SagaFlow.Participants.Messaging;
using SagaFlow.Payment.Messaging;
using SagaFlow.Payment.Repositories;
using SagaFlow.Payment.Services;
using SagaFlow.Inventory.Messaging;
using SagaFlow.Inventory.Repositories;
using SagaFlow.Inventory.Services;
using SagaFlow.Shipment.Messaging;
using SagaFlow.Notification.Messaging;
using SagaFlow.ManagerApproval.Messaging;
using SagaFlow.ManagerApproval.Repositories;
using SagaFlow.Warehouse;

namespace SagaFlow.Samples.Tests;

public sealed class ParticipantBindingTests
{
    private static readonly Guid BusinessId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrderId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly string[] Commands = ["AuthorizePayment", "RefundPayment", "RefundReturnPayment",
        "ReserveInventory", "ReleaseInventory", "RestockReturn", "CreateShipment", "CancelShipment",
        "SendOrderNotification", "NotifyManager", "NotifyReturnAccepted", "NotifyReturnRejected",
        "RequestManagerApproval", "InspectReturn"];

    private static object Expected(string name) => name switch
    {
        "AuthorizePayment" => new AuthorizePaymentRequest(BusinessId, 12.50m),
        "RefundPayment" => new RefundPaymentRequest(BusinessId),
        "RefundReturnPayment" => new RefundReturnPaymentRequest(BusinessId, OrderId, 12.50m),
        "ReserveInventory" => new ReserveInventoryRequest(BusinessId, [new("sku", 2)]),
        "ReleaseInventory" => new ReleaseInventoryRequest(BusinessId),
        "RestockReturn" => new RestockReturnRequest(BusinessId, OrderId, [new("sku", 2)]),
        "CreateShipment" => new CreateShipmentRequest(BusinessId, "customer"),
        "CancelShipment" => new CancelShipmentRequest(BusinessId),
        "SendOrderNotification" => new SendOrderNotificationRequest(BusinessId, "customer"),
        "NotifyManager" => new NotifyManagerRequest(BusinessId, 1001L),
        "NotifyReturnAccepted" or "NotifyReturnRejected" => new NotifyReturnRequest(BusinessId, OrderId, "customer"),
        "RequestManagerApproval" => new RequestManagerApprovalRequest(BusinessId, 1001L),
        "InspectReturn" => new InspectReturnRequest(BusinessId, OrderId),
        _ => throw new ArgumentException(name)
    };

    // Contract fixtures are independent of request serialization, so renamed fields cannot round-trip unnoticed.
    private static JsonObject Body(string name)
    {
        object payload = name switch
        {
            "AuthorizePayment" => new { id = BusinessId, total = 12.50m },
            "RefundPayment" or "ReleaseInventory" or "CancelShipment" => new { id = BusinessId },
            "RefundReturnPayment" => new { returnId = BusinessId, orderId = OrderId, amount = 12.50m },
            "ReserveInventory" => new { id = BusinessId, items = new[] { new { productId = "sku", quantity = 2 } } },
            "RestockReturn" => new { returnId = BusinessId, orderId = OrderId, items = new[] { new { productId = "sku", quantity = 2 } } },
            "CreateShipment" or "SendOrderNotification" => new { id = BusinessId, customerId = "customer" },
            "NotifyManager" or "RequestManagerApproval" => new { id = BusinessId, totalQuantity = 1001L },
            "NotifyReturnAccepted" or "NotifyReturnRejected" => new { returnId = BusinessId, orderId = OrderId, customerId = "customer" },
            "InspectReturn" => new { returnId = BusinessId, orderId = OrderId },
            _ => throw new ArgumentException(name)
        };
        return JsonSerializer.SerializeToNode(payload, Web)!.AsObject();
    }

    private static object Bind(ParticipantCommand command) => command.Name switch
    {
        "AuthorizePayment" => command.Bind<AuthorizePaymentRequest>(),
        "RefundPayment" => command.Bind<RefundPaymentRequest>(),
        "RefundReturnPayment" => command.Bind<RefundReturnPaymentRequest>(),
        "ReserveInventory" => command.Bind<ReserveInventoryRequest>(),
        "ReleaseInventory" => command.Bind<ReleaseInventoryRequest>(),
        "RestockReturn" => command.Bind<RestockReturnRequest>(),
        "CreateShipment" => command.Bind<CreateShipmentRequest>(),
        "CancelShipment" => command.Bind<CancelShipmentRequest>(),
        "SendOrderNotification" => command.Bind<SendOrderNotificationRequest>(),
        "NotifyManager" => command.Bind<NotifyManagerRequest>(),
        "NotifyReturnAccepted" or "NotifyReturnRejected" => command.Bind<NotifyReturnRequest>(),
        "RequestManagerApproval" => command.Bind<RequestManagerApprovalRequest>(),
        "InspectReturn" => command.Bind<InspectReturnRequest>(),
        _ => throw new ArgumentException(command.Name)
    };

    private static ParticipantCommand Command(string name, JsonElement payload) => new()
    {
        Name = name, Payload = payload,
        Headers = new() { SagaInstanceId = "technical-saga", CorrelationId = "technical-correlation", StepId = "technical-step" }
    };

    public static IEnumerable<object[]> ValidCases() => Commands.Select(name => new object[] { name });

    [Theory, MemberData(nameof(ValidCases))]
    public void EveryCommandBindsItsOwnBusinessSchemaAndIgnoresUnrelatedFields(string name)
    {
        var expected = Expected(name);
        var body = Body(name);
        body["unrelated"] = "allowed for the existing full Order/Return payload";
        var command = Command(name, JsonSerializer.SerializeToElement(body));
        var context = command.Headers;
        var actual = Bind(command);
        Assert.Equal(expected.GetType(), actual.GetType());
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected, Web), JsonSerializer.SerializeToElement(actual, Web)));
        Assert.Same(context, command.Headers); // Binding leaves transport context untouched.
    }

    public static IEnumerable<object[]> InvalidFieldCases()
    {
        foreach (var name in Commands)
        foreach (var field in Body(name))
        foreach (var invalid in new[] { "missing", "null", "wrong-type" })
            yield return [name, field.Key, invalid];
    }

    [Theory, MemberData(nameof(InvalidFieldCases))]
    public void MissingNullOrIncorrectlyTypedRequiredFieldsFailExplicitly(string name, string field, string invalid)
    {
        var body = Body(name);
        if (invalid == "missing") body.Remove(field);
        else body[field] = invalid == "null" ? null : JsonValue.Create(true);
        var error = Assert.Throws<JsonException>(() => Bind(Command(name, JsonSerializer.SerializeToElement(body))));
        Assert.Contains(name, error.Message);
        Assert.Contains(Expected(name).GetType().Name, error.Message);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("1")]
    [InlineData("\"\"")]
    public void NonObjectBodiesAreRejected(string json) =>
        Assert.Throws<JsonException>(() => Command("RefundPayment", JsonSerializer.Deserialize<JsonElement>(json)).Bind<RefundPaymentRequest>());

    [Fact]
    public void EmptyBodyAndMalformedJsonCannotBecomeDefaultRequests()
    {
        Assert.Throws<JsonException>(() => Command("RefundPayment", default).Bind<RefundPaymentRequest>());
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<JsonElement>(""));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<JsonElement>("{"));
    }

    [Fact]
    public void PascalAndMixedCaseFieldsBindButNumericStringsAreNotCoerced()
    {
        var body = JsonSerializer.SerializeToElement(new { ID = BusinessId, ToTaL = 12.50m });
        Assert.Equal(new AuthorizePaymentRequest(BusinessId, 12.50m), Command("AuthorizePayment", body).Bind<AuthorizePaymentRequest>());
        var quoted = JsonSerializer.SerializeToElement(new { id = BusinessId, total = "12.50" });
        Assert.Throws<JsonException>(() => Command("AuthorizePayment", quoted).Bind<AuthorizePaymentRequest>());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"productId\":\"sku\"}")]
    [InlineData("{\"quantity\":2}")]
    [InlineData("{\"productId\":null,\"quantity\":2}")]
    [InlineData("{\"productId\":\"sku\",\"quantity\":\"2\"}")]
    public void NestedInventoryFieldsAreRequiredAndTyped(string item)
    {
        var body = Body("ReserveInventory");
        body["items"] = new JsonArray(JsonNode.Parse(item));
        Assert.Throws<JsonException>(() => Bind(Command("ReserveInventory", JsonSerializer.SerializeToElement(body))));
    }

    [Theory]
    [InlineData("ReserveInventory")]
    [InlineData("RestockReturn")]
    public void NullInventoryEntriesFailAtTypedBoundaryBeforeBusinessMutation(string name)
    {
        var repository = new InMemoryInventoryRepository();
        var handler = new InventoryCommandHandler(new InventoryService(repository));
        var body = Body(name);
        body["items"] = new JsonArray((JsonNode?)null);
        Assert.Throws<ArgumentException>(() => handler.Handle(Command(name, JsonSerializer.SerializeToElement(body))));
        Assert.Null(repository.Find(BusinessId.ToString("N")));
        Assert.Null(repository.FindReturnRestock(BusinessId.ToString("N")));
    }

    [Fact]
    public void ValidationStillRunsInExistingServicesWithoutMutation()
    {
        var payments = new InMemoryPaymentRepository();
        var payment = new PaymentCommandHandler(new PaymentService(payments));
        Assert.Throws<ArgumentOutOfRangeException>(() => payment.Handle(Command("AuthorizePayment", JsonSerializer.SerializeToElement(new { id = BusinessId, total = 0m }))));
        Assert.Null(payments.Find(BusinessId.ToString("N")));
        var approvals = new InMemoryApprovalRepository();
        var approval = new ApprovalCommandHandler(approvals, TimeProvider.System, new DeferredReplies());
        Assert.Throws<InvalidOperationException>(() => approval.Handle(Command("RequestManagerApproval", JsonSerializer.SerializeToElement(new { id = BusinessId, totalQuantity = 0 })), "source.outcomes"));
    }

    [Theory]
    [InlineData("123")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void NonStringFailureControlRemainsIgnored(string value)
    {
        var body = Body("AuthorizePayment");
        body["failureAt"] = JsonNode.Parse(value);
        var command = Command("AuthorizePayment", JsonSerializer.SerializeToElement(body));
        Assert.False(SampleFailureInjection.IsRequested(command, "payment", "AuthorizePayment"));
    }

    [Fact]
    public void FailureInjectionRemainsAnEarlyReturnAndCompensationStillUsesHeaders()
    {
        var payment = new PaymentCommandHandler(new PaymentService(new InMemoryPaymentRepository()));
        Assert.False(payment.Handle(Command("AuthorizePayment", JsonSerializer.SerializeToElement(new { failureAt = "payment" }))).IsSuccess);
        var command = new ParticipantCommand { Name = "RefundReturnPayment", Headers = new() { IsCompensation = true }, Payload = JsonSerializer.SerializeToElement(Expected("RefundReturnPayment"), Web) };
        Assert.Throws<InvalidOperationException>(() => payment.Handle(command));
    }
}
