using SagaFlow.Participants.Messaging;

namespace Sample.Participants.Messaging;

internal interface IParticipantCommandHandler
{
    Task<ParticipantOutcome> HandleAsync(ParticipantCommand command, CancellationToken cancellationToken);
}
