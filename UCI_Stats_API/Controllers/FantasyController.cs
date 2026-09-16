using Microsoft.AspNetCore.Mvc;
using UCI_Stats_API.Features.Fantasy;

namespace UCI_Stats_API.Controllers;

[ApiController]
[Route("api/v1/fantasy")]
public sealed class FantasyController(FantasyService fantasy) : ControllerBase
{
    [HttpGet("current")]
    public async Task<IActionResult> Current(CancellationToken token) => Ok(await fantasy.CurrentAsync(token));

    [HttpGet("riders")]
    public async Task<IActionResult> Riders(CancellationToken token) => Ok((await fantasy.CurrentAsync(token)).Riders);

    [HttpGet("recommendation")]
    public async Task<IActionResult> Recommendation(CancellationToken token)
    {
        // Return diagnostics as well as a nullable recommendation, never an empty successful team.
        return Ok(await fantasy.CurrentAsync(token));
    }

    [HttpGet("rules")]
    public IActionResult Rules() => Ok(FantasyService.Rules());
}
