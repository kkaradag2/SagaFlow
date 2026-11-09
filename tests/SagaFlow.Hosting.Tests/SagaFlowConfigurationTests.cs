using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using SagaFlow.Hosting.Dashboard;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SagaFlow.Engine.ProcessDefinition;
using SagaFlow.Hosting.DependencyInjection;
using SagaFlow.Hosting.Endpoints;
using SagaFlow.Storage.InMemory.DependencyInjection;

namespace SagaFlow.Hosting.Tests;

public sealed class SagaFlowConfigurationTests
{
    [Fact]
    public async Task SingleFileAndInfrastructureSettingsAreResolvedFromTheHost()
    {
        var root = Directory.CreateTempSubdirectory("sagaflow-registration-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "selected.yaml"), """
                name: order-process
                domain: order
                version: "1.0"
                steps:
                  - name: work
                    command: Work
                    transitions:
                      - event: success
                        nextStep: completed
                """);
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "ignored.yaml"), "steps: [");
            var builder = Builder(root.FullName);
            builder.Services.AddSagaFlow(options => options.LoadConfiguration("default.yaml"));
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SagaFlow:DefinitionPath"] = "selected.yaml",
                ["SagaFlow:Publication:Enabled"] = "true",
                ["SagaFlow:Publication:IncludeBigPictureSources"] = "true",
                ["SagaFlow:Discovery:RequestTimeout"] = "00:00:02",
                ["SagaFlow:Discovery:MaximumDepth"] = "2",
                ["SagaFlow:Dashboard:Enabled"] = "true"
            });
            await using var app = builder.Build();
            Assert.NotNull(app.MapSagaFlowDefinitionDiscoveryIfEnabled());
            await app.StartAsync();

            var options = app.Services.GetRequiredService<SagaFlowOptions>();
            Assert.Equal("selected.yaml", options.DefinitionPath);
            Assert.True(options.Publication.IncludeBigPictureSources);
            Assert.Equal(TimeSpan.FromSeconds(2), options.Discovery.RequestTimeout);
            Assert.Equal(2, options.Discovery.MaximumDepth);
            Assert.Null(app.Services.GetService<IHubContext<SagaFlowHub>>());
            Assert.Equal("order-process", Assert.Single(app.Services.GetRequiredService<ProcessDefinitionRegistry>().Snapshot()).Definition.Name);
            await app.StopAsync();
        }
        finally { root.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrInvalidSingleYamlFailsStartup(bool invalid)
    {
        var root = Directory.CreateTempSubdirectory("sagaflow-invalid-definition-");
        try
        {
            const string name = "selected.yaml";
            if (invalid) await File.WriteAllTextAsync(Path.Combine(root.FullName, name), "steps: [");
            var builder = Builder(root.FullName);
            builder.Services.AddSagaFlow(options => options.LoadConfiguration(name));
            await using var app = builder.Build();
            Assert.Null(app.MapSagaFlowDefinitionDiscoveryIfEnabled());
            var error = await Record.ExceptionAsync(() => app.StartAsync());
            if (invalid) Assert.IsType<DefinitionValidationException>(error);
            else Assert.IsType<FileNotFoundException>(error);
            Assert.Contains(name, error!.Message);
        }
        finally { root.Delete(recursive: true); }
    }

    private static WebApplicationBuilder Builder(string contentRoot)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = contentRoot });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSagaFlowInMemoryStorage();
        return builder;
    }
}
