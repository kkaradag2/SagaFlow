using RabbitMQ.Client;

namespace SagaFlow.Samples.Tests;

public sealed class SampleRabbitMqFactAttribute : FactAttribute
{
    public SampleRabbitMqFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SAGAFLOW_SAMPLE_RABBITMQ_INTEGRATION"),
                "true",
                StringComparison.OrdinalIgnoreCase))
            Skip = "Set SAGAFLOW_SAMPLE_RABBITMQ_INTEGRATION=true to run the sample end-to-end test.";
    }
}

internal static class SampleRabbitMqTestIsolation
{
    public static string RequireVirtualHost()
    {
        var vhost = Environment.GetEnvironmentVariable("SAGAFLOW_RABBITMQ_TEST_VHOST");
        if (string.IsNullOrWhiteSpace(vhost) || vhost == "/" ||
            !string.Equals(vhost, Environment.GetEnvironmentVariable("RabbitMq__VirtualHost"), StringComparison.Ordinal) ||
            !Uri.TryCreate(Environment.GetEnvironmentVariable("ConnectionStrings__RabbitMq"), UriKind.Absolute, out var uri) ||
            new ConnectionFactory { Uri = uri }.VirtualHost != vhost)
            throw new InvalidOperationException("Sample integration tests require the same dedicated vhost in SAGAFLOW_RABBITMQ_TEST_VHOST, RabbitMq__VirtualHost and ConnectionStrings__RabbitMq. Use scripts/run-rabbitmq-integration.ps1.");
        return vhost;
    }
}
