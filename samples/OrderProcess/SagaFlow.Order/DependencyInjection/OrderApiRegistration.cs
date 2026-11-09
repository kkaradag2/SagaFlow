using System.Text.Json;
using FluentValidation.AspNetCore;
using Microsoft.AspNetCore.Mvc;
using SagaFlow.Order.Contracts.Responses;

namespace SagaFlow.Order.DependencyInjection;

/// <summary>Registers Order controllers and preserves the Order API validation response contract.</summary>
internal static class OrderApiRegistration
{
    internal static IServiceCollection AddOrderApi(this IServiceCollection services)
    {
        services
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
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen();
        services.AddFluentValidationAutoValidation();
        return services;
    }

    private static string ToJsonFieldPath(string field) =>
        string.Join('.', field.Split('.').Select(JsonNamingPolicy.CamelCase.ConvertName));
}
