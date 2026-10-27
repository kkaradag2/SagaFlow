using RabbitMQ.Client;

namespace SagaFlow.Messaging.RabbitMQ.Tests;

public sealed class RabbitMqBrokerFixture : IAsyncLifetime
{
    private IConnection? _connection;

    public string HostName { get; } = Environment.GetEnvironmentVariable("SAGAFLOW_RABBITMQ_HOST") ?? "localhost";
    public int Port { get; } = int.TryParse(Environment.GetEnvironmentVariable("SAGAFLOW_RABBITMQ_PORT"), out var port)
        ? port
        : AmqpTcpEndpoint.UseDefaultPort;
    public string UserName { get; } = Environment.GetEnvironmentVariable("SAGAFLOW_RABBITMQ_USERNAME") ?? "guest";
    public string Password { get; } = Environment.GetEnvironmentVariable("SAGAFLOW_RABBITMQ_PASSWORD") ?? "guest";
    public IConnection Connection => _connection
        ?? throw new InvalidOperationException("RabbitMQ integration tests are not enabled.");

    public async Task InitializeAsync()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SAGAFLOW_RABBITMQ_INTEGRATION"),
                "true",
                StringComparison.OrdinalIgnoreCase)) return;

        var factory = CreateFactory("sagaflow-integration-tests");
        _connection = await factory.CreateConnectionAsync();
    }

    public async Task DisposeAsync()
    {
        if (_connection is null) return;
        if (_connection.IsOpen) await _connection.CloseAsync();
        await _connection.DisposeAsync();
    }

    public ConnectionFactory CreateFactory(string name) => new()
    {
        HostName = HostName,
        Port = Port,
        UserName = UserName,
        Password = Password,
        VirtualHost = "/",
        ClientProvidedName = name,
        AutomaticRecoveryEnabled = true,
        TopologyRecoveryEnabled = true
    };
}

[CollectionDefinition(Name)]
public sealed class RabbitMqIntegrationCollection : ICollectionFixture<RabbitMqBrokerFixture>
{
    public const string Name = "RabbitMQ integration";
}
