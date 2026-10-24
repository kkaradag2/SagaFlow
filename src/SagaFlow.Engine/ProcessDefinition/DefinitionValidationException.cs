namespace SagaFlow.Engine.ProcessDefinition;

public sealed class DefinitionValidationException : Exception
{
    public IReadOnlyList<string> Errors { get; }

    public DefinitionValidationException(IEnumerable<string> errors)
        : this(Array.AsReadOnly(errors.ToArray())) { }

    private DefinitionValidationException(IReadOnlyList<string> errors)
        : base(string.Join(Environment.NewLine, errors)) => Errors = errors;
}
