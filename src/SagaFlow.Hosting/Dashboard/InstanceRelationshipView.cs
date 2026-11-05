using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Core.Abstractions;
using Microsoft.AspNetCore.Http;

namespace SagaFlow.Hosting.Dashboard;

/// <summary>One host-local observation. Source remote status and target local status are never interchangeable.</summary>
public sealed record InstanceRelationshipView(
    string InvocationId, string Perspective, ProcessIdentity Source, string SourceSagaInstanceId,
    string SourceStep, string SourceCorrelationId, ProcessIdentity Target, string? TargetSagaInstanceId,
    string? TargetCorrelationId, string? LastKnownStatus, string? TargetLocalStatus,
    DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt, DateTimeOffset? LastObservedAt,
    InvocationReconciliation? Reconciliation, string? RejectionReason = null)
{
    internal static async Task<IResult> ReadAsync(string instanceId, HttpContext context, ISagaStateStore store)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.Headers.Allow = "GET";
            return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
        }
        var query = context.Request.Query;
        var start = 0; var size = 50;
        if ((query.ContainsKey("offset") && !int.TryParse(query["offset"], out start))
            || (query.ContainsKey("limit") && !int.TryParse(query["limit"], out size))
            || start < 0 || size is < 1 or > 100) return Results.BadRequest();
        string? Filter(string key) => query.ContainsKey(key) ? query[key].ToString() : null;
        var perspective = Filter("perspective");
        if (perspective is not null and not "Source" and not "Target") return Results.BadRequest();
        // One instance lookup, never ListAsync/discovery/remote I/O; filters refer to the local owner.
        var instance = await store.FindAsync(instanceId, context.RequestAborted);
        if (instance is null || (Filter("domain") is { } domain && domain != instance.DefinitionDomain)
            || (Filter("process") is { } process && process != instance.DefinitionName)
            || (Filter("version") is { } version && version != instance.DefinitionVersion)) return Results.NotFound();
        var page = From(instance).Where(row => perspective is null || row.Perspective == perspective)
            .Skip(start).Take(size + 1).ToArray();
        return Results.Ok(new InstanceRelationshipPage(instance.Id, page.Take(size).ToArray(),
            page.Length > size ? (long?)start + size : null));
    }
    // Reuse invocation records, not a second relationship ledger. Ignore inconsistent legacy linkage.
    internal static IEnumerable<InstanceRelationshipView> From(SagaInstance instance)
    {
        bool Local(ProcessIdentity identity) => identity.Domain == instance.DefinitionDomain
            && identity.Process == instance.DefinitionName && identity.Version == instance.DefinitionVersion;
        if (instance.IncomingInvocation is { } incoming && Local(incoming.Identity.Target))
        {
            var identity = incoming.Identity;
            yield return new(identity.InvocationId, "Target", identity.Source, identity.SourceSagaInstanceId,
                identity.SourceStep, identity.CorrelationId, identity.Target, instance.Id, instance.CorrelationId,
                null, instance.Status.ToString(), incoming.CreatedAt, instance.UpdatedAt, null, null);
        }
        foreach (var step in instance.Steps)
        {
            if (step.Invocation is not { } request) continue;
            var identity = request.Identity;
            if (!Local(identity.Source) || identity.SourceSagaInstanceId != instance.Id
                || identity.SourceStep != step.StepName || identity.CorrelationId != instance.CorrelationId) continue;
            yield return new(identity.InvocationId, "Source", identity.Source, instance.Id,
                identity.SourceStep, identity.CorrelationId, identity.Target, step.InvocationProgress?.TargetSagaInstanceId,
                null, step.InvocationProgress?.Status.ToString(), null, step.InvocationProgress?.CreatedAt,
                step.InvocationProgress?.UpdatedAt, step.InvocationProgress?.LastObservedAt, step.Reconciliation, step.InvocationProgress?.RejectionReason);
        }
    }
}

public sealed record InstanceRelationshipPage(string LocalInstanceId, IReadOnlyList<InstanceRelationshipView> Items, long? NextOffset);
