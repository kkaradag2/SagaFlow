using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.State;
using SagaFlow.Hosting.Dashboard;
using SagaFlow.Hosting.DependencyInjection;

namespace SagaFlow.Hosting.Endpoints;

public static class SagaFlowDashboardEndpoints
{
    public static IEndpointRouteBuilder MapSagaFlowDashboard(this IEndpointRouteBuilder endpoints)
    {
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
