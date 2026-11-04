using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using System.Runtime.ExceptionServices;

namespace SagaFlow.Messaging.RabbitMQ;

internal sealed class RabbitMqConnectionProvider : IAsyncDisposable
{
    private readonly RabbitMqOptions? _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private bool _disposed;

    public RabbitMqConnectionProvider(IConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        OwnsConnection = false;
    }

    public RabbitMqConnectionProvider(RabbitMqOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        OwnsConnection = true;
    }

    internal bool OwnsConnection { get; }
    internal IConnection? Connection => _connection;

    public async ValueTask<IConnection> GetConnectionAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_connection is { IsOpen: true }) return _connection;
        if (!OwnsConnection)
            throw new InvalidOperationException("The host-owned RabbitMQ connection is not open.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connection is { IsOpen: true }) return _connection;

            var factory = new ConnectionFactory
            {
                HostName = _options!.HostName,
                Port = _options.Port,
                UserName = _options.UserName,
                Password = _options.Password,
                VirtualHost = _options.VirtualHost,
                ClientProvidedName = _options.ClientProvidedName,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true
            };

            _connection = await factory.CreateConnectionAsync(cancellationToken);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (OwnsConnection && _connection is not null)
        {
            Exception? failure = null;
            try { if (_connection.IsOpen) await _connection.CloseAsync(CancellationToken.None); }
            catch (Exception error) when (error is AlreadyClosedException or ObjectDisposedException) { }
            catch (Exception error) { failure = error; }
            try { await _connection.DisposeAsync(); }
            catch (Exception error) when (error is AlreadyClosedException or ObjectDisposedException) { }
            catch (Exception error) { failure ??= error; }
            _gate.Dispose();
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            return;
        }

        _gate.Dispose();
    }
}
