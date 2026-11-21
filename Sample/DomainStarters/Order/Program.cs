using Microsoft.AspNetCore.Mvc.Controllers;
using SagaFlow.Hosting.Endpoints;
using Sample.DomainStarters.Order.Data;
using Sample.DomainStarters.Order.Observability;
using Sample.DomainStarters.Order.Orchestration;

var builder = WebApplication.CreateBuilder(args);
builder.AddOrderLogging();
builder.Services.AddControllers();
builder.Services.AddSwaggerGen(options =>
    options.DocInclusionPredicate((_, endpoint) => endpoint.ActionDescriptor is ControllerActionDescriptor));
builder.Services.AddOrderServices(builder.Configuration);
builder.Services.AddOrderSagaFlow(builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapSagaFlowDefinitionDiscoveryIfEnabled();
app.MapControllers();
app.Run();
