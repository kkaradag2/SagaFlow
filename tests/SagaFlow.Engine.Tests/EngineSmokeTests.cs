using SagaFlow.Engine.Coordination;

namespace SagaFlow.Engine.Tests;

public sealed class EngineSmokeTests
{
    [Fact]
    public void SagaCoordinator_CanBeCreated()
    {
        var coordinator = new SagaCoordinator();

        Assert.Equal(nameof(SagaCoordinator), coordinator.Name);
    }
}
