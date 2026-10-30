using Microsoft.AspNetCore.Http;
using SagaFlow.Dashboard.Assets;
using SagaFlow.Core.Abstractions;
using SagaFlow.Hosting.Endpoints;
using SagaFlow.Engine.ProcessDefinition;
using Microsoft.Extensions.DependencyInjection;

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

        if (string.Equals(requestPath, dashboardPath, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Redirect($"{dashboardPath}/");
            return;
        }

        var assetRoot = _options.AssetRoot ?? EmbeddedDashboardAssets.GetDashboardAssetPath();
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
            await SendFileAsync(context, indexPath);
            return;
        }

        var assetPath = Path.GetFullPath(Path.Combine(rootPath, relativePath));

        if (assetPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase) && File.Exists(assetPath))
        {
            await SendFileAsync(context, assetPath);
            return;
        }

        if (relativePath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await SendFileAsync(context, indexPath);
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
