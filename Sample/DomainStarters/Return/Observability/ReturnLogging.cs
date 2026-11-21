using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

namespace Sample.DomainStarters.Return.Observability;

internal static class ReturnLogging
{
    public static void AddReturnLogging(this WebApplicationBuilder builder)
    {
        var endpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"]
            ?? throw new InvalidOperationException("OpenTelemetry:OtlpEndpoint is required.");

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("newsample-return"))
            .WithLogging(logging => logging.AddOtlpExporter(exporter =>
            {
                exporter.Endpoint = new Uri(endpoint);
                exporter.Protocol = OtlpExportProtocol.Grpc;
            }), options => options.IncludeFormattedMessage = true);
    }
}
