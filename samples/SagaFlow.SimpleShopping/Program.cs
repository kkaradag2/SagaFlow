using SagaFlow.Hosting;
using SagaFlow.Hosting.DependencyInjection;
using SagaFlow.Engine.Discovery;
using SagaFlow.Engine.ProcessDefinition;
using SagaFlow.Hosting.Endpoints;

var builder = WebApplication.CreateBuilder(args);
var publishDefinitions = builder.Configuration.GetValue<bool>("SagaFlow:PublishDefinitions");

builder.Services.AddSagaFlow(options =>
{
    options.LoadFromDirectory(builder.Configuration["SagaFlow:DefinitionDirectory"] ?? "saga");
    options.Publication.Enabled = publishDefinitions;
    options.Publication.IncludeBigPictureSources = builder.Configuration.GetValue<bool>("SagaFlow:PublishBigPictureSources");
    options.Publication.PublishedDefinitions.Add(new("order", "purchase-process", "1.0"));
});

var app = builder.Build();
if (publishDefinitions) app.MapSagaFlowDefinitionDiscovery();

app.UseSagaFlowDashboard(options =>
{
    options.Path = "/sagaflow";
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapGet("/demo/purchase-definition", (ProcessDefinitionRegistry registry) =>
{
    var definition = registry.Find(new("order", "purchase-process", "1.0"))!.Definition;
    return Results.Text(new DefinitionYamlCodec().Serialize(definition, includeBigPicture: false), "application/yaml");
});

app.MapGet("/demo/definition-graph", async (ProcessDefinitionRegistry registry, DefinitionDiscovery discovery, CancellationToken cancellationToken) =>
    Results.Ok(await discovery.DiscoverAsync(registry.Find(new("order", "purchase-process", "1.0"))!.Definition, cancellationToken)));

app.Run();
