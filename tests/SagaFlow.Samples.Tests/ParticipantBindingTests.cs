using System.Text.Json;
using SagaFlow.Participants.Messaging;

namespace SagaFlow.Samples.Tests;

public sealed class ParticipantBindingTests
{
    [Fact]
    public void BindsBusinessFieldsWithoutUsingExecutionMetadata()
    {
        var order = Guid.NewGuid();
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { id = order, total = 12.50m, ignored = true }));
        var command = new ParticipantCommand
        {
            Name = "AuthorizePayment",
            Payload = json.RootElement,
            Headers = new() { SagaInstanceId = Guid.NewGuid().ToString("N") }
        };
        Assert.Equal(new PaymentRequest(order, 12.50m), command.Bind<PaymentRequest>());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"id":"11111111-1111-1111-1111-111111111111","total":"12.50"}""")]
    [InlineData("""{"id":"11111111-1111-1111-1111-111111111111","total":null}""")]
    public void RejectsInvalidBusinessPayloads(string payload)
    {
        using var json = JsonDocument.Parse(payload);
        var command = new ParticipantCommand { Name = "AuthorizePayment", Payload = json.RootElement };
        var error = Assert.Throws<JsonException>(() => command.Bind<PaymentRequest>());
        Assert.Contains("AuthorizePayment", error.Message);
    }

    private sealed record PaymentRequest(Guid Id, decimal Total, string? FailStep = null);
}
