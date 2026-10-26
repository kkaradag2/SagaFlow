using SagaFlow.Notification;
using SagaFlow.Notification.Repositories;
using SagaFlow.Notification.Services;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<INotificationRepository, InMemoryNotificationRepository>();
builder.Services.AddSingleton<INotificationService, NotificationService>();
builder.Services.AddHostedService<Worker>();
await builder.Build().RunAsync();
