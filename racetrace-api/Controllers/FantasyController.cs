using Microsoft.AspNetCore.Mvc;
using RaceTrace.Api.Features.Fantasy;

namespace RaceTrace.Api.Controllers;

[ApiController]
[Route("api/v1/fantasy")]
public sealed class FantasyController(FantasyService fantasy) : ControllerBase
{
    [HttpGet("current")]
    [ProducesResponseType(typeof(RaceTrace.Api.Models.FantasyCurrentDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Current(CancellationToken token) => Ok(await fantasy.CurrentAsync(token));

    [HttpGet("riders")]
    [ProducesResponseType(typeof(IReadOnlyList<RaceTrace.Api.Models.FantasyRiderDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Riders(CancellationToken token) => Ok((await fantasy.CurrentAsync(token)).Riders);

    [HttpGet("recommendation")]
    [ProducesResponseType(typeof(RaceTrace.Api.Models.FantasyCurrentDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Recommendation(CancellationToken token)
    {
        // Return diagnostics as well as a nullable recommendation, never an empty successful team.
        return Ok(await fantasy.CurrentAsync(token));
    }

    [HttpGet("rules")]
    [ProducesResponseType(typeof(RaceTrace.Api.Models.FantasyRulesDto), StatusCodes.Status200OK)]
    public IActionResult Rules() => Ok(FantasyService.Rules());
}
