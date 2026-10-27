using SagaFlow.Participants.Messaging;

namespace SagaFlow.Payment;

public sealed class Worker(
    IParticipantCommandHandler handler,
    IConfiguration configuration,
    ILogger<RabbitMqParticipantWorker> logger)
    : RabbitMqParticipantWorker(handler, configuration, logger);
