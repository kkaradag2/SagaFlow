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
