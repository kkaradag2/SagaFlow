using SagaFlow.Engine.Coordination;
using SagaFlow.Engine.ContextState;
using SagaFlow.Engine.Messaging;
using SagaFlow.Engine.Persistence.InMemory;
using SagaFlow.Engine.ProcessDefinition;

namespace SagaFlow.Engine.Tests;

public sealed class EngineSmokeTests
{
    [Fact]
    public void SagaCoordinator_CanBeCreated()
    {
        var store = new InMemorySagaStateStore();
        var clock = new SystemSagaClock();
        var coordinator = new SagaCoordinator(new ProcessDefinitionRegistry(), store, new SagaInstanceManager(clock),
            new SagaStepExecutor(store, new CommandDispatcher(), clock), new TransitionResolver(), new ProcessStateManager(), clock);

        Assert.Equal(nameof(SagaCoordinator), coordinator.Name);
    }
}
