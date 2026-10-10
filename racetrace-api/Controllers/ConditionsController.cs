using Microsoft.AspNetCore.Mvc;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Controllers;

[ApiController]
[Route("api/v1/conditions")]
public sealed class ConditionsController : ControllerBase
{
    private readonly ConditionsService _conditions;

    public ConditionsController(ConditionsService conditions)
    {
        _conditions = conditions;
    }

    [HttpGet]
    [ProducesResponseType(typeof(RaceTrace.Api.Models.ListResponse<System.Text.Json.Nodes.JsonObject>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        return Ok(await _conditions.ListConditionsAsync(Request.Query, cancellationToken));
    }
}
