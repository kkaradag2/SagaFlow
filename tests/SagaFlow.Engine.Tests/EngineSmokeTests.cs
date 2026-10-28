using SagaFlow.Engine.Coordination;
using SagaFlow.Engine.ContextState;
using SagaFlow.Engine.Messaging;
using SagaFlow.Engine.Persistence.InMemory;
using SagaFlow.Engine.ProcessDefinition;
using SagaFlow.Engine.Consistency;

namespace SagaFlow.Engine.Tests;

public sealed class EngineSmokeTests
{
    [Fact]
    public void SagaCoordinator_CanBeCreated()
    {
        var store = new InMemorySagaStateStore();
        var clock = new SystemSagaClock();
        var dispatcher = new CommandDispatcher();
        var coordinator = new SagaCoordinator(new ProcessDefinitionRegistry(), store, new SagaInstanceManager(clock),
            new SagaStepExecutor(store, dispatcher, clock), new TransitionResolver(), new ProcessStateManager(),
            new CompensationManager(store, dispatcher, clock), clock);

        Assert.Equal(nameof(SagaCoordinator), coordinator.Name);
    }
}
