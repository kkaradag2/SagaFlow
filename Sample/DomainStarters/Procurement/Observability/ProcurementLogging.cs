using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

namespace Sample.DomainStarters.Procurement.Observability;

internal static class ProcurementLogging
{
    public static void AddProcurementLogging(this WebApplicationBuilder builder)
    {
        var endpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"]
            ?? throw new InvalidOperationException("OpenTelemetry:OtlpEndpoint is required.");

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("newsample-procurement"))
            .WithLogging(logging => logging.AddOtlpExporter(exporter =>
            {
                exporter.Endpoint = new Uri(endpoint);
                exporter.Protocol = OtlpExportProtocol.Grpc;
            }), options => options.IncludeFormattedMessage = true);
    }
}
