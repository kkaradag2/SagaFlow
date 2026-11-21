using Microsoft.AspNetCore.Mvc.Controllers;
using SagaFlow.Hosting.Endpoints;
using Sample.DomainStarters.Return.Data;
using Sample.DomainStarters.Return.Observability;
using Sample.DomainStarters.Return.Orchestration;

var builder = WebApplication.CreateBuilder(args);
builder.AddReturnLogging();
builder.Services.AddControllers();
builder.Services.AddSwaggerGen(options =>
    options.DocInclusionPredicate((_, endpoint) => endpoint.ActionDescriptor is ControllerActionDescriptor));
builder.Services.AddReturnServices(builder.Configuration);
builder.Services.AddReturnSagaFlow(builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapSagaFlowDefinitionDiscoveryIfEnabled();
app.MapControllers();
app.Run();
