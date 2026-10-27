using SagaFlow.Payment;
using SagaFlow.Payment.Messaging;
using SagaFlow.Payment.Repositories;
using SagaFlow.Payment.Services;
using SagaFlow.Participants.Messaging;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<IPaymentRepository, InMemoryPaymentRepository>();
builder.Services.AddSingleton<IPaymentService, PaymentService>();
builder.Services.AddSingleton<IParticipantCommandHandler, PaymentCommandHandler>();
builder.Services.AddHostedService<Worker>();
await builder.Build().RunAsync();
