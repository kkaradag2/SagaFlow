using System.Text.Json.Serialization;
using SagaFlow.ManagerApproval.Messaging;
using SagaFlow.ManagerApproval.Repositories;
using SagaFlow.ManagerApproval.Services;
using SagaFlow.Participants.Messaging;

namespace SagaFlow.ManagerApproval;

public class ManagerApprovalProgram
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddControllers().AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<InMemoryApprovalRepository>();
        builder.Services.AddSingleton<IParticipantCommandHandler, ApprovalCommandHandler>();
        builder.Services.AddSingleton<IApprovalOutcomePublisher, RabbitMqApprovalOutcomePublisher>();
        builder.Services.AddScoped<ApprovalDecisionService>();
        builder.Services.AddHostedService<RabbitMqParticipantWorker>();
        var app = builder.Build();
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }
        app.MapControllers();
        app.Run();
    }
}
