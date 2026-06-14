using SagaFlow.Hosting;
using SagaFlow.Hosting.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSagaFlow(options =>
{
    options.LoadFromYaml("saga/purchase-process.yaml");
});

var app = builder.Build();

app.UseSagaFlowDashboard(options =>
{
    options.Path = "/sagaflow";
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapGet("/demo/purchase-definition", async (IWebHostEnvironment environment) =>
{
    var path = Path.Combine(environment.ContentRootPath, "saga", "purchase-process.yaml");
    var content = await File.ReadAllTextAsync(path);

    return Results.Text(content, "application/yaml");
});

app.Run();
