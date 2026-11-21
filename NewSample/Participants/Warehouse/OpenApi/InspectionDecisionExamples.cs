using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Sample.Participants.Warehouse.Controllers;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Sample.Participants.Warehouse.OpenApi;

internal sealed class InspectionDecisionExamples : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.MethodInfo.DeclaringType != typeof(InspectionsController) ||
            context.MethodInfo.Name != nameof(InspectionsController.Decide) ||
            operation.RequestBody is not OpenApiRequestBody { Content: { } content } requestBody)
            return;

        requestBody.Description = "Use `accept` to accept the request or `reject` to reject it.";
        foreach (var mediaType in content.Values)
        {
            mediaType.Examples = new Dictionary<string, IOpenApiExample>
            {
                ["accept"] = new OpenApiExample
                {
                    Summary = "Accept the request",
                    Value = new JsonObject { ["decision"] = "accept" }
                },
                ["reject"] = new OpenApiExample
                {
                    Summary = "Reject the request",
                    Value = new JsonObject { ["decision"] = "reject" }
                }
            };
        }
    }
}
