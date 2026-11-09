using Microsoft.Extensions.DependencyInjection;

namespace SagaFlow.Hosting.DependencyInjection;

/// <summary>Selects the optional SagaFlow message transport.</summary>
public sealed class SagaFlowMessagingOptions
{
    internal Action<IServiceCollection>? Register { get; private set; }

    internal void Select(Action<IServiceCollection> register)
    {
        if (Register is not null)
            throw new InvalidOperationException("A SagaFlow message transport is already selected.");
        Register = register;
    }
}
