using SagaFlow.Notification;
using SagaFlow.Notification.Messaging;
using SagaFlow.Notification.Repositories;
using SagaFlow.Notification.Services;
using SagaFlow.Participants.Messaging;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<INotificationRepository, InMemoryNotificationRepository>();
builder.Services.AddSingleton<INotificationService, NotificationService>();
builder.Services.AddSingleton<IParticipantCommandHandler, NotificationCommandHandler>();
builder.Services.AddHostedService<Worker>();
await builder.Build().RunAsync();
