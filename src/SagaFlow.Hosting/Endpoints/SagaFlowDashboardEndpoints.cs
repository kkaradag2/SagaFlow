using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Routing;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.State;
using SagaFlow.Hosting.Dashboard;
using SagaFlow.Hosting.DependencyInjection;
using SagaFlow.Engine.ProcessDefinition;
using SagaFlow.Engine.Discovery;
using SagaFlow.Hosting.Definitions;

namespace SagaFlow.Hosting.Endpoints;

public static class SagaFlowDashboardEndpoints
{
    public static IEndpointRouteBuilder MapSagaFlowDashboard(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/sagaflow/api/definitions", (ProcessDefinitionRegistry registry) =>
            Results.Ok(ToDefinitions(registry)));
        endpoints.MapGet("/sagaflow/api/definition-graph", async (
            string domain, string process, string version, ProcessDefinitionRegistry registry,
            DefinitionDiscovery discovery, CancellationToken cancellationToken) =>
        {
            var graph = await ToDefinitionGraphAsync(registry, discovery, domain, process, version,
                cancellationToken);
            return graph is null ? Results.NotFound() : Results.Ok(graph);
        });
        endpoints.MapGet("/sagaflow/api/instances", async (ISagaStateStore store, CancellationToken cancellationToken) =>
        {
            var instances = await store.ListAsync(cancellationToken);
            return Results.Ok(instances.Select(ToSummary));
        });
        endpoints.MapGet("/sagaflow/api/instances/{instanceId}", async (string instanceId, ISagaStateStore store, SagaFlowOptions options, CancellationToken cancellationToken) =>
        {
            var instance = await store.FindAsync(instanceId, cancellationToken);
            return instance is null ? Results.NotFound() : Results.Ok(ToDetail(instance, options.Dashboard.DescribeDomainStarter));
        });
        return endpoints;
    }

    // Presentation of the already parsed registry snapshots; no loading, discovery, or runtime execution.
    internal static object[] ToDefinitions(ProcessDefinitionRegistry registry) => registry.Snapshot()
        .Where(entry => entry.Origin == DefinitionOrigin.Local)
        .Select(entry => ToDashboardDefinition(entry.Definition)).ToArray();

    private static object ToDashboardDefinition(SagaFlow.Core.Definitions.SagaDefinition definition) => new
        {
            definition.Name,
            definition.Domain,
            definition.Version,
            Steps = definition.Steps.Select(step => new
            {
                step.Name, step.Command, step.TimeoutLimit, step.MaxAttempts, step.Compensation,
                Transitions = step.Transitions.Select(transition => new
                {
                    transition.Event, transition.When, transition.Else, transition.NextStep,
                    Condition = transition.Condition is null ? null : new
                    {
                        transition.Condition.Path, transition.Condition.Operator, transition.Condition.Value,
                        // Browsers must not round large numeric literals when displaying a comparison.
                        ValueJson = transition.Condition.Value.GetRawText()
                    }
                })
            }),
            BigPicture = definition.BigPicture.Select(reference => new
            {
                reference.Domain, reference.Process, reference.Version, reference.Relationship
            }).ToArray()
        };

    internal static async Task<object?> ToDefinitionGraphAsync(ProcessDefinitionRegistry registry,
        DefinitionDiscovery discovery,
        string domain, string process, string version, CancellationToken cancellationToken)
    {
        var local = registry.Find(new(domain, process, version));
        if (local is null || local.Origin != DefinitionOrigin.Local) return null;
        var graph = await discovery.DiscoverAsync(local.Definition, cancellationToken);
        return new
        {
            Nodes = graph.Nodes.Select(node => new { node.Id.Domain, Process = node.Id.Name, node.Id.Version,
                Origin = node.Origin.ToString(), Definition = ToDashboardDefinition(node.Definition) }),
            Edges = graph.Edges.Select(edge => new { edge.Source, edge.TargetDomain, edge.TargetProcess,
                edge.RequestedVersion, edge.Target, edge.Relationship }),
            Failures = graph.Failures.Select(failure => new { failure.Source, failure.TargetDomain,
                failure.TargetProcess, failure.RequestedVersion, Kind = failure.Kind.ToString(), failure.Message })
        };
    }

    internal static object ToSummary(SagaInstance instance) => new
    {
        instance.Id,
        ProcessName = instance.DefinitionName,
        instance.CorrelationId,
        Status = instance.Status.ToString(),
        instance.CurrentStep,
        instance.CreatedAt,
        instance.UpdatedAt
    };

    internal static object ToDetail(SagaInstance instance, Func<SagaInstance, SagaDomainStarterDisplay?>? describeDomainStarter = null) => new
    {
        instance.Id,
        ProcessName = instance.DefinitionName,
        instance.DefinitionDomain,
        instance.DefinitionVersion,
        instance.CorrelationId,
        Status = instance.Status.ToString(),
        instance.CurrentStep,
        instance.CreatedAt,
        instance.UpdatedAt,
        instance.FailureReason,
        DomainStarter = describeDomainStarter?.Invoke(instance) ?? new SagaDomainStarterDisplay(),
        Steps = instance.Steps.Select(step => new
        {
            step.StepName,
            step.Command,
            Status = step.Status.ToString(),
            step.ExecutionOrder,
            step.StartedAt,
            step.CompletedAt,
            TimeoutLimit = step.TimeoutLimit?.ToString(),
            step.TimedOutAt,
            step.Input,
            step.Result
        }),
        Compensations = instance.Compensations.Select(compensation => new
        {
            compensation.StepId,
            compensation.Command,
            Status = compensation.Status.ToString(),
            compensation.ExecutionOrder,
            compensation.StartedAt,
            compensation.CompletedAt,
            compensation.Result
        }),
        DomainRollback = instance.DomainRollback is null ? null : new
        {
            Status = instance.DomainRollback.Status.ToString(),
            instance.DomainRollback.StartedAt,
            instance.DomainRollback.CompletedAt,
            instance.DomainRollback.Error
        }
    };
}
