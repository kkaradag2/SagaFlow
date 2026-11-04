using System.Reflection;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace SagaFlow.Messaging.RabbitMQ.Tests;

public sealed class RabbitMqTransportShutdownTests
{
    [Fact]
    public async Task ClosedChannelSkipsConsumerCancellation()
    {
        var channel = ChannelProxy.Create(open: false);
        await RabbitMqTransport.CancelConsumerAsync(channel.Channel, "consumer", CancellationToken.None);
        Assert.Equal(0, channel.CancelCalls);
    }

    [Fact]
    public async Task ChannelClosingBetweenOpenCheckAndCancelIsExpectedDuringShutdown()
    {
        var channel = ChannelProxy.Create(open: true, cancelError: new AlreadyClosedException(
            new ShutdownEventArgs(ShutdownInitiator.Application, 200, "closed")));
        await RabbitMqTransport.CancelConsumerAsync(channel.Channel, "consumer", CancellationToken.None);
        Assert.Equal(1, channel.CancelCalls);
    }

    [Fact]
    public async Task UnexpectedCancellationFailureRemainsVisible()
    {
        var channel = ChannelProxy.Create(open: true, cancelError: new InvalidOperationException("unexpected"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RabbitMqTransport.CancelConsumerAsync(channel.Channel, "consumer", CancellationToken.None));
        Assert.Equal("unexpected", error.Message);
    }

    public class ChannelProxy : DispatchProxy
    {
        private bool _open;
        private Exception? _cancelError;
        public int CancelCalls { get; private set; }
        public IChannel Channel { get; private set; } = null!;

        public static ChannelProxy Create(bool open, Exception? cancelError = null)
        {
            var channel = DispatchProxy.Create<IChannel, ChannelProxy>();
            var proxy = (ChannelProxy)(object)channel;
            proxy.Channel = channel;
            proxy._open = open;
            proxy._cancelError = cancelError;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "get_IsOpen") return _open;
            if (targetMethod?.Name == "BasicCancelAsync")
            {
                CancelCalls++;
                if (_cancelError is not null) throw _cancelError;
                return Task.CompletedTask;
            }
            throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
