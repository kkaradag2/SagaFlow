namespace SagaFlow.Messaging.RabbitMQ.Tests;

public sealed class RabbitMqFactAttribute : FactAttribute
{
    public RabbitMqFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SAGAFLOW_RABBITMQ_INTEGRATION"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            Skip = "Set SAGAFLOW_RABBITMQ_INTEGRATION=true to run against a real RabbitMQ broker.";
        }
    }
}
