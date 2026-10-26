using SagaFlow.Core.Definitions;

namespace SagaFlow.Engine.ProcessDefinition;

internal static class DefinitionSnapshot
{
    // Defensive deep copies preserve the public init-only DTO shape without exposing mutable lists.
    public static SagaDefinition Copy(SagaDefinition d, bool includeBigPicture = true) => new()
    {
        Name = d.Name, Domain = d.Domain, Version = d.Version,
        Steps = Array.AsReadOnly(d.Steps.Select(s => new SagaStepDefinition
        {
            Name = s.Name, Command = s.Command, TimeoutLimit = s.TimeoutLimit, MaxAttempts = s.MaxAttempts,
            Compensation = s.Compensation is null ? null : new() { Command = s.Compensation.Command },
            Transitions = Array.AsReadOnly(s.Transitions.Select(t => new SagaTransitionDefinition
                { Event = t.Event, When = t.When, Else = t.Else, NextStep = t.NextStep }).ToArray())
        }).ToArray()),
        BigPicture = Array.AsReadOnly(includeBigPicture ? d.BigPicture.Select(s => new BigPictureSourceDefinition
            { Domain = s.Domain, Process = s.Process, Version = s.Version, Endpoint = s.Endpoint }).ToArray() : [])
    };
}
