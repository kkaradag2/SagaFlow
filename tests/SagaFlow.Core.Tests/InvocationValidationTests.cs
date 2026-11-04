using SagaFlow.Core.Messages;
namespace SagaFlow.Core.Tests;
public sealed class InvocationValidationTests
{
    [Fact]
    public void RejectsInvalidIdentitiesAndNonterminalOutcomes()
    {
        Assert.Throws<ArgumentException>(() => new ProcessIdentity("", "process", "1.0"));
        Assert.Throws<ArgumentException>(() => new ProcessIdentity("domain", "process", "latest"));
        var process = new ProcessIdentity("domain", "process", "1.0");
        Assert.Throws<ArgumentException>(() => new ProcessInvocationIdentity("bad", "trace", process, "saga", "step", process));
        var identity = new ProcessInvocationIdentity(Guid.NewGuid().ToString(), "trace", process, "source", "step", process);
        var message = new InvocationMessage(identity, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow);
        Assert.NotEqual(identity.InvocationId, message.MessageId);
        Assert.Throws<ArgumentException>(() => new ProcessInvocationAccepted(message, ""));
        Assert.Throws<ArgumentException>(() => new ProcessInvocationTerminalOutcome(message, "target", ExternalProcessStatus.Accepted));
        var outcome = new ProcessInvocationTerminalOutcome(message, "target", ExternalProcessStatus.Completed);
        message.ValidateAgainst(identity);
        Assert.Throws<InvalidOperationException>(() => message.ValidateAgainst(new(identity.InvocationId, "other", process, "source", "step", process)));
        Assert.Equal("target", outcome.TargetSagaInstanceId);
        var request = new ProcessInvocationRequest(identity, System.Text.Json.JsonSerializer.SerializeToElement(new { Value = 1 }));
        var copy = System.Text.Json.JsonSerializer.Deserialize<ProcessInvocationRequest>(System.Text.Json.JsonSerializer.Serialize(request))!;
        Assert.Equal(request.Message, copy.Message);
        Assert.Equal(identity, copy.Identity);
    }
}
