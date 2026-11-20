using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Sample.Participants.Payment.Data;
using Sample.Participants.Payment.Messaging;
using Sample.Participants.Payment.Services;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
var connectionString = builder.Configuration.GetConnectionString("Payment")
    ?? throw new InvalidOperationException("ConnectionStrings:Payment is required.");

builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton(PaymentRabbitMqSettings.Read(builder.Configuration));
builder.Services.AddSingleton<PaymentRepository>();
builder.Services.AddSingleton<PaymentService>();
builder.Services.AddSingleton<PaymentCommandHandler>();
builder.Services.AddHostedService<PaymentWorker>();

await builder.Build().RunAsync();
