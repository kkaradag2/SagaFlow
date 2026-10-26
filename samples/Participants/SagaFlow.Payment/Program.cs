using SagaFlow.Payment;
using SagaFlow.Payment.Repositories;
using SagaFlow.Payment.Services;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<IPaymentRepository, InMemoryPaymentRepository>();
builder.Services.AddSingleton<IPaymentService, PaymentService>();
builder.Services.AddHostedService<Worker>();
await builder.Build().RunAsync();
