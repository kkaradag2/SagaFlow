using System.Text.Json;
using SagaFlow.ManagerApproval.Domain;
using SagaFlow.Warehouse;
using SagaFlow.Participants.Messaging;

namespace SagaFlow.Samples.Tests;

public sealed class DeferredMetadataIsolationTests
{
    [Theory]
    [InlineData(typeof(ApprovalRecord))]
    [InlineData(typeof(InspectionRecord))]
    public void BusinessModelContainsNeitherOrchestrationFieldsNorTransportTypes(Type businessType)
    {
        foreach (var property in businessType.GetProperties())
        {
            Assert.DoesNotContain(property.Name, new[] { "SagaInstanceId", "CorrelationId", "StepId", "ReplyTo", "TransportContext", "TransportHandle" });
            Assert.NotEqual(typeof(ParticipantContext), property.PropertyType);
            Assert.NotEqual(typeof(DeferredReplyHandle), property.PropertyType);
        }
        object record = businessType == typeof(ApprovalRecord)
            ? new ApprovalRecord(Guid.NewGuid(), 1001, DateTimeOffset.UtcNow)
            : new InspectionRecord(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(record));
        Assert.All(json.RootElement.EnumerateObject(), property => Assert.DoesNotContain(property.Name,
            new[] { "SagaInstanceId", "CorrelationId", "StepId", "ReplyTo", "TransportContext", "TransportHandle" }));
    }

    [Fact]
    public void TransportHandleKeepsExecutionAssociationOutsideBusinessRecordAndRejectsConflicts()
    {
        var replies = new DeferredReplies();
        ParticipantCommand Command(string saga, object data) => new() { Name = "InspectReturn", Payload = JsonSerializer.SerializeToElement(data),
            Headers = new() { SagaInstanceId = saga, CorrelationId = saga + "-correlation", StepId = "warehouse" } };
        var first = Command("first", new { returnId = 1 });
        var second = Command("second", new { returnId = 2 });
        var firstBusinessId = Guid.NewGuid(); var secondBusinessId = Guid.NewGuid(); var creates = 0;
        var firstHandle = replies.Register(first, "first.reply", () => { creates++; return firstBusinessId; });
        Assert.Equal(firstHandle, replies.Register(first, "first.reply", () => throw new Exception("Redelivery must not create a second record.")));
        var secondHandle = replies.Register(second, "second.reply", () => { creates++; return secondBusinessId; });
        Assert.NotEqual(firstHandle, secondHandle);
        Assert.Equal(firstHandle, replies.ForBusinessRecord(firstBusinessId));
        Assert.Equal(secondHandle, replies.ForBusinessRecord(secondBusinessId));
        Assert.Equal("second.reply", replies.Prepare(secondHandle, "inspection-result", new { accepted = true }).ReplyTo);
        Assert.Equal("first", replies.Prepare(firstHandle, "inspection-result", new { accepted = false }).Outcome.Headers.SagaInstanceId);
        Assert.Throws<InvalidOperationException>(() => replies.Register(first, "foreign.reply", () => Guid.NewGuid()));
        Assert.Throws<InvalidOperationException>(() => replies.Register(Command("first", new { returnId = 999 }), "first.reply", () => Guid.NewGuid()));
        Assert.Equal(2, creates);
        Assert.Throws<KeyNotFoundException>(() => new DeferredReplies().ForBusinessRecord(firstBusinessId));
    }
}
