using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Messages;
using SagaFlow.Engine.Messaging;
using SagaFlow.Hosting.DependencyInjection;
using SagaFlow.Messaging.RabbitMQ.DependencyInjection;
namespace SagaFlow.Messaging.RabbitMQ.Tests;
public sealed class ProcessInvocationRoutingTests
{
    [Fact]
    public void ProcessVersionRoutingIsDeterministicAndSeparateFromParticipantQueues()
    {
        var identity = new ProcessIdentity("domain", "process", "1.0");
        var queue = RabbitMqInvocationTransport.Queue(identity, true);
        Assert.Equal(queue, RabbitMqInvocationTransport.Queue(new("domain", "process", "1.0"), true));
        Assert.NotEqual(queue, RabbitMqInvocationTransport.Queue(new("domain", "process", "2.0"), true));
        Assert.NotEqual(queue, RabbitMqInvocationTransport.Queue(new("other", "process", "1.0"), true));
        Assert.NotEqual(queue, RabbitMqInvocationTransport.Queue(identity, false));
        Assert.StartsWith("sagaflow.invocations.", queue);
        Assert.True(RabbitMqInvocationTransport.Queue(new(new string('a', 128), new string('b', 128), "1.0"), true).Length < 255);
    }
    [Fact]
    public void OptInRegistersGenericInvocationServicesAndDefaultKeepsParticipantTransport()
    {
        var builder = Host.CreateApplicationBuilder(); builder.Services.AddSagaFlow();
        builder.Services.AddSagaFlowRabbitMq(options => options.EnableProcessInvocations = true);
        using var host = builder.Build();
        Assert.NotNull(host.Services.GetRequiredService<IProcessInvocationBus>());
        using var scope = host.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ProcessInvocationReceiver>());
        var plain = Host.CreateApplicationBuilder(); plain.Services.AddSagaFlow(); plain.Services.AddSagaFlowRabbitMq(_ => { });
        using var plainHost = plain.Build();
        Assert.Null(plainHost.Services.GetService<IProcessInvocationBus>());
        Assert.NotNull(plainHost.Services.GetRequiredService<ISagaMessageBus>());
    }
}
