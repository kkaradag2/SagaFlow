using RabbitMQ.Client;

namespace SagaFlow.Messaging.RabbitMQ;

public sealed class RabbitMqOptions
{
    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = AmqpTcpEndpoint.UseDefaultPort;
    public string UserName { get; set; } = ConnectionFactory.DefaultUser;
    public string Password { get; set; } = ConnectionFactory.DefaultPass;
    public string VirtualHost { get; set; } = ConnectionFactory.DefaultVHost;
    public string ClientProvidedName { get; set; } = "sagaflow";
    public string CommandExchange { get; set; } = "sagaflow.commands";
    public ushort PrefetchCount { get; set; } = 16;

    internal void Validate(bool managedConnection)
    {
        if (managedConnection) ArgumentException.ThrowIfNullOrWhiteSpace(HostName);
        ArgumentException.ThrowIfNullOrWhiteSpace(CommandExchange);
        if (PrefetchCount == 0) throw new ArgumentOutOfRangeException(nameof(PrefetchCount), "PrefetchCount must be positive.");
    }
}
