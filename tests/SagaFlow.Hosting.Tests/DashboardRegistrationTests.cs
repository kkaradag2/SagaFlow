using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SagaFlow.Hosting.DependencyInjection;
using SagaFlow.Storage.InMemory.DependencyInjection;

namespace SagaFlow.Hosting.Tests;

public sealed class DashboardRegistrationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("admin/sagas")]
    [InlineData("/admin//sagas")]
    [InlineData("/admin/{saga}")]
    [InlineData("/admin/../sagas")]
    [InlineData("/admin/sagas?view=all")]
    public void InvalidDashboardPathFailsRegistration(string path)
    {
        var services = new ServiceCollection();
        var error = Assert.Throws<InvalidOperationException>(() => services.AddSagaFlow(options =>
            options.UseDashboard(dashboard => dashboard.Path = path)));
        Assert.Contains("SagaFlow Dashboard path", error.Message);
    }

    [Fact]
    public async Task DashboardPathCannotShadowAnApplicationEndpoint()
    {
        var builder = Builder();
        builder.Services.AddSagaFlow(options =>
        {
            options.Storage.UseInMemory();
            options.UseDashboard(dashboard => dashboard.Path = "/admin/sagas");
        });
        await using var app = builder.Build();
        app.MapGet("/admin/Sagas/report/{id}", (string id) => id);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
        Assert.Contains("conflicts with application route '/admin/Sagas/report/{id}'", error.Message);
    }

    [Fact]
    public async Task DashboardEndpointsAndAssetsHonorTheHostAuthorizationFallback()
    {
        var builder = Builder();
        builder.Services.AddAuthentication("cookie").AddCookie("cookie", options =>
        {
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
        });
        builder.Services.AddAuthorizationBuilder().SetFallbackPolicy(
            new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        builder.Services.AddSagaFlow(options =>
        {
            options.Storage.UseInMemory();
            options.UseDashboard();
        });
        await using var app = builder.Build();
        app.MapGet("/other", () => "application route").AllowAnonymous();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };

        Assert.Equal("application route", await client.GetStringAsync("/other"));
        using var html = await client.GetAsync("/sagaflow/");
        using var api = await client.GetAsync("/sagaflow/api/instances");
        using var hub = await client.PostAsync("/sagaflow/hub/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.Unauthorized, html.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, hub.StatusCode);
    }

    private static WebApplicationBuilder Builder()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        return builder;
    }
}
