using Microsoft.Extensions.DependencyInjection;

namespace SagaFlow.Hosting.DependencyInjection;

/// <summary>Selects the singleton store that owns Saga coordination state.</summary>
public sealed class SagaFlowStorageOptions
{
    internal Action<IServiceCollection>? Register { get; private set; }
    private string? _selection;

    internal void Select(string selection, Action<IServiceCollection> register)
    {
        if (Register is not null)
            throw new InvalidOperationException($"SagaFlow storage already selected '{_selection}'. Remove the '{selection}' call. Choose exactly one provider in options.Storage.");
        _selection = selection;
        Register = register;
    }
}
