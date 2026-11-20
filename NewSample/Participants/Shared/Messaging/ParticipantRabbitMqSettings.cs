using Microsoft.Extensions.Configuration;

namespace Sample.Participants.Messaging;

internal sealed record ParticipantRabbitMqSettings(
    Uri ConnectionUri, string ClientProvidedName, string CommandExchange, string QueueName,
    string RoutingKey, IReadOnlySet<string> OutcomeQueues)
{
    public static ParticipantRabbitMqSettings Read(IConfiguration configuration)
    {
        var section = configuration.GetRequiredSection("RabbitMq");
        string Required(string key) => !string.IsNullOrWhiteSpace(section[key])
            ? section[key]!
            : throw new InvalidOperationException($"RabbitMq:{key} is required.");

        var connectionString = Required("ConnectionString");
        if (!Uri.TryCreate(connectionString, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("amqp" or "amqps") || string.IsNullOrEmpty(uri.Host))
            throw new InvalidOperationException("RabbitMq:ConnectionString must be an AMQP URI.");

        var outcomeQueues = section.GetSection("OutcomeQueues").Get<string[]>();
        if (outcomeQueues is not { Length: > 0 } || outcomeQueues.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("RabbitMq:OutcomeQueues requires at least one queue name.");

        return new(uri, Required("ClientProvidedName"), Required("CommandExchange"),
            Required("QueueName"), Required("RoutingKey"), outcomeQueues.ToHashSet(StringComparer.Ordinal));
    }
}
