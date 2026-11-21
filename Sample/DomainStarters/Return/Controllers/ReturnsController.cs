using Microsoft.AspNetCore.Mvc;
using Sample.DomainStarters.Return.Contracts;
using Sample.DomainStarters.Return.Services;

namespace Sample.DomainStarters.Return.Controllers;

[ApiController]
[Route("returns")]
public sealed class ReturnsController(IReturnService returns) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<CreateReturnResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<CreateReturnResponse>> Create(
        CreateReturnRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var created = await returns.CreateAsync(request, cancellationToken);
            return CreatedAtAction(nameof(Get), new { returnId = created.ReturnId }, created);
        }
        catch (ReturnSagaStartException error)
        {
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: error.Message,
                instance: Url.Action(nameof(Get), new { returnId = error.ReturnId }),
                extensions: new Dictionary<string, object?>
                {
                    ["returnId"] = error.ReturnId,
                    ["sagaInstanceId"] = error.SagaInstanceId
                });
        }
    }

    [HttpGet("{returnId:guid}")]
    [ProducesResponseType<ReturnStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReturnStatusResponse>> Get(Guid returnId, CancellationToken cancellationToken)
    {
        var view = await returns.FindAsync(returnId, cancellationToken);
        return view is null ? NotFound() : Ok(view);
    }
}
