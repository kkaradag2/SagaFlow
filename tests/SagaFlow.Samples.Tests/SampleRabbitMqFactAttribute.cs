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
            !string.Equals(vhost, Environment.GetEnvironmentVariable("RabbitMq__VirtualHost"), StringComparison.Ordinal))
            throw new InvalidOperationException("Sample integration tests require matching dedicated SAGAFLOW_RABBITMQ_TEST_VHOST and RabbitMq__VirtualHost; use scripts/run-rabbitmq-integration.ps1.");
        return vhost;
    }
}
