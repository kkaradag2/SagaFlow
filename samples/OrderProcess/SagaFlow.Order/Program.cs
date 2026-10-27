using System.Text.Json;
using FluentValidation;
using FluentValidation.AspNetCore;
using Microsoft.AspNetCore.Mvc;
using SagaFlow.Hosting.DependencyInjection;
using SagaFlow.Hosting.Endpoints;
using SagaFlow.Messaging.RabbitMQ.DependencyInjection;
using SagaFlow.Order.Contracts.Responses;
using SagaFlow.Order.Domain;
using SagaFlow.Order.Repositories;
using SagaFlow.Order.Services;
using SagaFlow.Order.Validators;

var builder = WebApplication.CreateBuilder(args);
var publishDefinitions = builder.Configuration.GetValue<bool>("SagaFlow:PublishDefinitions");
var definitionDirectory = builder.Configuration["SagaFlow:DefinitionDirectory"] ?? "saga";
var publishedProcess = builder.Configuration["SagaFlow:PublishedProcess"] ?? "order-process";

builder.Services
    .AddControllers()
    .ConfigureApiBehaviorOptions(options =>
    {
        options.InvalidModelStateResponseFactory = context =>
        {
            var errors = context.ModelState
                .Where(entry => entry.Value?.Errors.Count > 0)
                .SelectMany(entry => entry.Value!.Errors.Select(error => new ErrorDetail(
                    ToJsonFieldPath(entry.Key),
                    string.IsNullOrWhiteSpace(error.ErrorMessage)
                        ? "The supplied value is invalid."
                        : error.ErrorMessage)))
                .ToArray();

            var response = new ErrorResponse(
                "validation_error",
                "Request validation failed.",
                errors,
                context.HttpContext.TraceIdentifier);

            return new BadRequestObjectResult(response);
        };
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddSingleton<IOrderRepository, InMemoryOrderRepository>();
builder.Services.AddSingleton<IValidator<CreateOrderRequest>, CreateOrderRequestValidator>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddSagaFlow(options =>
{
    options.LoadFromDirectory(definitionDirectory);
    options.Publication.Enabled = publishDefinitions;
    options.Publication.IncludeBigPictureSources = builder.Configuration.GetValue<bool>("SagaFlow:PublishBigPictureSources");
    options.Publication.PublishedDefinitions.Add(new("order", publishedProcess, "1.0"));
});
if (builder.Configuration.GetValue("RabbitMq:Enabled", true))
    builder.Services.AddSagaFlowRabbitMq(builder.Configuration.GetRequiredSection("RabbitMq"));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (publishDefinitions) app.MapSagaFlowDefinitionDiscovery();
app.MapControllers();

app.Run();

static string ToJsonFieldPath(string field) =>
    string.Join('.', field.Split('.').Select(JsonNamingPolicy.CamelCase.ConvertName));

public partial class Program;
