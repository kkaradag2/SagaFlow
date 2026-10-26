using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Definitions;
using SagaFlow.Engine.ProcessDefinition;
using SagaFlow.Hosting.DependencyInjection;

namespace SagaFlow.Hosting.Tests;

public sealed class DefinitionLoaderRegistrationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HostedStartupInvokesCustomRegisteredLoader(bool directoryMode, bool registerBeforeSagaFlow)
    {
        var directory = Directory.CreateTempSubdirectory("sagaflow-di-").FullName;
        try
        {
            var firstPath = Path.Combine(directory, "a.yaml");
            await File.WriteAllTextAsync(firstPath, Yaml("first"));
            if (directoryMode) await File.WriteAllTextAsync(Path.Combine(directory, "b.yml"), Yaml("second"));
            var loader = new DecoratingLoader();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
            if (registerBeforeSagaFlow) builder.Services.AddSingleton<ISagaDefinitionLoader>(loader);
            builder.Services.AddSagaFlow(options =>
            {
                if (directoryMode) options.LoadFromDirectory(directory);
                else options.LoadFromYaml(firstPath);
            });
            if (!registerBeforeSagaFlow) builder.Services.AddSingleton<ISagaDefinitionLoader>(loader);
            await using var app = builder.Build();
            await app.StartAsync();
            Assert.Equal(directoryMode ? new[] { "a.yaml", "b.yml" } : new[] { "a.yaml" }, loader.Paths.Select(Path.GetFileName));
            Assert.All(app.Services.GetRequiredService<ProcessDefinitionRegistry>().Snapshot(), registered =>
            {
                Assert.Equal("work", registered.Definition.Steps[0].Name);
                Assert.Equal(DefinitionOrigin.Local, registered.Origin);
            });
            await app.StopAsync();
        }
        finally { Directory.Delete(directory, true); }
    }

    private static string Yaml(string name) => $$"""
        name: {{name}}
        version: "1.0"
        domain: order
        steps:
          - name: work
            command: Work
            transitions:
              - event: success
                nextStep: completed
        """;

    private sealed class DecoratingLoader : ISagaDefinitionLoader
    {
        public List<string> Paths { get; } = [];
        public async ValueTask<SagaDefinition> LoadAsync(string path, CancellationToken cancellationToken = default)
        {
            Paths.Add(path);
            var parsed = await new YamlSagaDefinitionLoader().LoadAsync(path, cancellationToken);
            return new()
            {
                Name = parsed.Name, Domain = parsed.Domain, Version = parsed.Version,
                Steps = parsed.Steps, BigPicture = parsed.BigPicture
            };
        }
    }
}
