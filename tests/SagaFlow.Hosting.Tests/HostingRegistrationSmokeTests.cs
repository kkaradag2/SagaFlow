using Microsoft.Extensions.DependencyInjection;
using SagaFlow.Engine.Coordination;
using SagaFlow.Hosting.DependencyInjection;

namespace SagaFlow.Hosting.Tests;

public sealed class HostingRegistrationSmokeTests
{
    [Fact]
    public void AddSagaFlow_RegistersCoordinator()
    {
        var services = new ServiceCollection();

        services.AddSagaFlow(options => options.LoadFromYaml("saga/purchase-process.yaml"));

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<SagaCoordinator>());
    }
}
