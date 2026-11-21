using Microsoft.AspNetCore.Builder;
using SagaFlow.Hosting.DependencyInjection;
using SagaFlow.Hosting.Endpoints;
using SagaFlow.Storage.InMemory.DependencyInjection;

namespace SagaFlow.Hosting.Tests;

internal static class DefinitionPublisherProgram
{
    public static Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSagaFlow(options => options.Storage.UseInMemory());
        var app = builder.Build();
        app.MapSagaFlowDefinitionDiscoveryIfEnabled();
        return app.RunAsync();
    }
}
