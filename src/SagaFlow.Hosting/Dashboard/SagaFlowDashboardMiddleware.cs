using Microsoft.AspNetCore.Http;
using SagaFlow.Dashboard.Assets;
using SagaFlow.Core.Abstractions;
using SagaFlow.Hosting.Endpoints;
using SagaFlow.Engine.ProcessDefinition;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Encodings.Web;
using SagaFlow.Engine.Discovery;
using SagaFlow.Hosting.Definitions;

namespace SagaFlow.Hosting.Dashboard;

public sealed class SagaFlowDashboardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly SagaFlowDashboardOptions _options;

    public SagaFlowDashboardMiddleware(RequestDelegate next, SagaFlowDashboardOptions options)
    {
        _next = next;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context, ISagaStateStore store)
    {
        var requestPath = context.Request.Path.Value ?? string.Empty;
        var dashboardPath = SagaFlowDashboardPath.Normalize(_options.Path);

        if (string.Equals(requestPath, $"{dashboardPath}/api/definitions", StringComparison.OrdinalIgnoreCase))
        {
            if (!HttpMethods.IsGet(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                context.Response.Headers.Allow = "GET";
                return;
            }
            var registry = context.RequestServices.GetRequiredService<ProcessDefinitionRegistry>();
            await Results.Ok(SagaFlowDashboardEndpoints.ToDefinitions(registry)).ExecuteAsync(context);
            return;
        }

        if (string.Equals(requestPath, $"{dashboardPath}/api/definition-graph", StringComparison.OrdinalIgnoreCase))
        {
            if (!HttpMethods.IsGet(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                context.Response.Headers.Allow = "GET";
                return;
            }
            var graph = await SagaFlowDashboardEndpoints.ToDefinitionGraphAsync(
                context.RequestServices.GetRequiredService<ProcessDefinitionRegistry>(),
                context.RequestServices.GetRequiredService<DefinitionDiscovery>(),
                context.Request.Query["domain"].ToString(), context.Request.Query["process"].ToString(),
                context.Request.Query["version"].ToString(), context.RequestAborted);
            await (graph is null ? Results.NotFound() : Results.Ok(graph)).ExecuteAsync(context);
            return;
        }

        var apiPath = $"{dashboardPath}/api/instances";
        if (string.Equals(requestPath, apiPath, StringComparison.OrdinalIgnoreCase))
        {
            var instances = await store.ListAsync(context.RequestAborted);
            await Results.Ok(instances.Select(SagaFlowDashboardEndpoints.ToSummary)).ExecuteAsync(context);
            return;
        }

        if (requestPath.StartsWith($"{apiPath}/", StringComparison.OrdinalIgnoreCase))
        {
            var instanceId = requestPath[(apiPath.Length + 1)..];
            const string relationshipSuffix = "/relationships";
            if (instanceId.EndsWith(relationshipSuffix, StringComparison.OrdinalIgnoreCase))
            {
                var relationships = await InstanceRelationshipView.ReadAsync(instanceId[..^relationshipSuffix.Length], context, store);
                await relationships.ExecuteAsync(context);
                return;
            }
            var instance = await store.FindAsync(instanceId, context.RequestAborted);
            var result = instance is null
                ? Results.NotFound()
                : Results.Ok(SagaFlowDashboardEndpoints.ToDetail(instance, _options.DescribeDomainStarter));
            await result.ExecuteAsync(context);
            return;
        }

        if (!IsDashboardRequest(requestPath, dashboardPath))
        {
            await _next(context);
            return;
        }

        await ServeAssetsAsync(context, _options, dashboardPath);
    }

    internal static async Task ServeAssetsAsync(HttpContext context, SagaFlowDashboardOptions options, string dashboardPath)
    {
        var requestPath = context.Request.Path.Value ?? string.Empty;
        var publicPath = context.Request.PathBase.Add(new PathString(dashboardPath)).ToUriComponent();
        if (string.Equals(requestPath, dashboardPath, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Redirect($"{publicPath}/");
            return;
        }

        var assetRoot = options.AssetRoot ?? EmbeddedDashboardAssets.GetDashboardAssetPath();
        var rootPath = Path.GetFullPath(assetRoot);
        var indexPath = Path.Combine(rootPath, "index.html");

        if (!File.Exists(indexPath))
        {
            await WriteMissingAssetsFallbackAsync(context);
            return;
        }

        var relativePath = requestPath[dashboardPath.Length..].TrimStart('/');
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            await SendIndexAsync(context, indexPath, publicPath);
            return;
        }

        var assetPath = Path.GetFullPath(Path.Combine(rootPath, relativePath));

        if (assetPath.StartsWith(Path.TrimEndingDirectorySeparator(rootPath) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) && File.Exists(assetPath))
        {
            await SendFileAsync(context, assetPath);
            return;
        }

        if (relativePath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await SendIndexAsync(context, indexPath, publicPath);
    }

    private static bool IsDashboardRequest(string requestPath, string dashboardPath)
    {
        return string.Equals(requestPath, dashboardPath, StringComparison.OrdinalIgnoreCase)
            || requestPath.StartsWith($"{dashboardPath}/", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task SendFileAsync(HttpContext context, string path)
    {
        context.Response.ContentType = GetContentType(path);
        await context.Response.SendFileAsync(path);
    }

    private static async Task SendIndexAsync(HttpContext context, string path, string dashboardPath)
    {
        var html = await File.ReadAllTextAsync(path, context.RequestAborted);
        var baseTag = $"<base href=\"{HtmlEncoder.Default.Encode(dashboardPath)}/\">";
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html.Replace("<head>", $"<head>{baseTag}", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task WriteMissingAssetsFallbackAsync(HttpContext context)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync("""
            <!doctype html>
            <html lang="en">
              <head><title>SagaFlow Dashboard</title></head>
              <body>
                <h1>SagaFlow Dashboard assets are not built yet. Run the dashboard client build.</h1>
              </body>
            </html>
            """);
    }

    private static string GetContentType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".css" => "text/css",
            ".html" => "text/html; charset=utf-8",
            ".js" => "application/javascript",
            ".json" => "application/json",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
    }
}
