using RabbitMQ.Client;
using System.Net.Security;

namespace SagaFlow.Messaging.RabbitMQ;

public sealed class RabbitMqOptions
{
    /// <summary>Managed broker URI using amqp or amqps. The URI takes precedence over individual connection fields.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Enable separate domain invocation request/outcome queues. Default keeps existing participant transport unchanged.</summary>
    public bool EnableProcessInvocations { get; set; }
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
        if (managedConnection) _ = CreateConnectionFactory();
        else if (ConnectionString is not null)
            throw new InvalidOperationException("RabbitMQ ConnectionString applies only to the managed connection mode.");
        ArgumentException.ThrowIfNullOrWhiteSpace(CommandExchange);
        if (PrefetchCount == 0) throw new ArgumentOutOfRangeException(nameof(PrefetchCount), "PrefetchCount must be positive.");
    }

    internal ConnectionFactory CreateConnectionFactory()
    {
        var factory = new ConnectionFactory
        {
            HostName = HostName,
            Port = Port,
            UserName = UserName,
            Password = Password,
            VirtualHost = VirtualHost,
            ClientProvidedName = ClientProvidedName,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true
        };
        if (ConnectionString is null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(HostName);
            return factory;
        }

        const string error = "RabbitMQ ConnectionString must be an absolute amqp:// or amqps:// URI with a host, valid port and optional single-segment virtual host. Query parameters and fragments are unsupported.";
        if (!Uri.TryCreate(ConnectionString, UriKind.Absolute, out var uri) || !uri.IsWellFormedOriginalString() ||
            uri.Scheme is not ("amqp" or "amqps") || string.IsNullOrWhiteSpace(uri.Host) || uri.Port == 0 ||
            ConnectionString.Contains('?') || ConnectionString.Contains('#') || uri.Segments.Length > 2 ||
            (uri.Segments.Length == 2 && uri.Segments[1].EndsWith('/')))
            throw new InvalidOperationException(error);

        try
        {
            // Use client defaults for URI components that were omitted.
            factory.UserName = ConnectionFactory.DefaultUser;
            factory.Password = ConnectionFactory.DefaultPass;
            factory.VirtualHost = ConnectionFactory.DefaultVHost;
            factory.Uri = uri;
            factory.Ssl.AcceptablePolicyErrors = SslPolicyErrors.None;
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException(error);
        }
        return factory;
    }
}
