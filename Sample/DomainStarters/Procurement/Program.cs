using Microsoft.AspNetCore.Mvc.Controllers;
using SagaFlow.Hosting.Endpoints;
using Sample.DomainStarters.Procurement.Data;
using Sample.DomainStarters.Procurement.Observability;
using Sample.DomainStarters.Procurement.Orchestration;

var builder = WebApplication.CreateBuilder(args);
builder.AddProcurementLogging();
builder.Services.AddControllers();
builder.Services.AddSwaggerGen(options =>
    options.DocInclusionPredicate((_, endpoint) => endpoint.ActionDescriptor is ControllerActionDescriptor));
builder.Services.AddProcurementServices(builder.Configuration);
builder.Services.AddProcurementSagaFlow(builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapSagaFlowDefinitionDiscoveryIfEnabled();
app.MapControllers();
app.Run();
