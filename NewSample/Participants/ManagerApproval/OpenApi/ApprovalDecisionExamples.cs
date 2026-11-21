using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Sample.Participants.ManagerApproval.Controllers;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Sample.Participants.ManagerApproval.OpenApi;

internal sealed class ApprovalDecisionExamples : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.MethodInfo.DeclaringType != typeof(ApprovalsController) ||
            context.MethodInfo.Name != nameof(ApprovalsController.Decide) ||
            operation.RequestBody is not OpenApiRequestBody { Content: { } content } requestBody)
            return;

        requestBody.Description = "Use `approve` to approve the request or `reject` to reject it.";
        foreach (var mediaType in content.Values)
        {
            mediaType.Examples = new Dictionary<string, IOpenApiExample>
            {
                ["approve"] = new OpenApiExample
                {
                    Summary = "Approve the request",
                    Value = new JsonObject { ["decision"] = "approve" }
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
