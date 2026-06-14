using SagaFlow.Core.Definitions;

namespace SagaFlow.Core.Tests;

public sealed class CoreModelSmokeTests
{
    [Fact]
    public void SagaDefinition_CanBeCreated()
    {
        var definition = new SagaDefinition { Name = "purchase-process" };

        Assert.Equal("purchase-process", definition.Name);
    }
}
