using SagaFlow.Participants.Messaging;

namespace Sample.Participants.Messaging;

internal interface IParticipantCommandHandler
{
    // A null result means the command was accepted and its outcome will be published later.
    Task<ParticipantOutcome?> HandleAsync(ParticipantCommand command, string replyTo, CancellationToken cancellationToken);
}
