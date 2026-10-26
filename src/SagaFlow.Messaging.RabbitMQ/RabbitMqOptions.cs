using RabbitMQ.Client;

namespace SagaFlow.Messaging.RabbitMQ;

public sealed class RabbitMqOptions
{
    private readonly Dictionary<string, string> _commandRoutes = new(StringComparer.Ordinal);

    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = AmqpTcpEndpoint.UseDefaultPort;
    public string UserName { get; set; } = ConnectionFactory.DefaultUser;
    public string Password { get; set; } = ConnectionFactory.DefaultPass;
    public string VirtualHost { get; set; } = ConnectionFactory.DefaultVHost;
    public string ClientProvidedName { get; set; } = "sagaflow";
    public string CommandExchange { get; set; } = "sagaflow.commands";
    public string OutcomeQueue { get; set; } = "sagaflow.outcomes";
    public ushort PrefetchCount { get; set; } = 16;
    public IReadOnlyDictionary<string, string> CommandRoutes => _commandRoutes;

    public RabbitMqOptions RouteCommand(string commandName, string queueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        _commandRoutes.Add(commandName, queueName);
        return this;
    }

    internal void Validate(bool managedConnection)
    {
        if (managedConnection) ArgumentException.ThrowIfNullOrWhiteSpace(HostName);
        ArgumentException.ThrowIfNullOrWhiteSpace(CommandExchange);
        ArgumentException.ThrowIfNullOrWhiteSpace(OutcomeQueue);
        if (PrefetchCount == 0) throw new ArgumentOutOfRangeException(nameof(PrefetchCount), "PrefetchCount must be positive.");
    }
}
