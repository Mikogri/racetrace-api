using Microsoft.AspNetCore.Mvc;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Controllers;

[ApiController]
[Route("api/v1/live/rounds/{roundId:long}/championship")]
public sealed class LiveChampionshipController(LiveChampionshipService championship) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(RaceTrace.Api.Services.ChampionshipContext), StatusCodes.Status200OK)]
    public async Task<IActionResult> Context(string roundId, [FromQuery] string category, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(category) || category.Length > 64) return BadRequest();
        return Ok(await championship.GetContextAsync(roundId, category, cancellationToken));
    }

    // Stateless projection of the same timing snapshots already displayed by the client.
    // No results, points or standings are written to the database.
    [HttpPost]
    [RequestSizeLimit(262144)]
    [ProducesResponseType(typeof(RaceTrace.Api.Services.ChampionshipProjection), StatusCodes.Status200OK)]
    public async Task<IActionResult> Project(string roundId, [FromQuery] string category,
        [FromBody] ChampionshipSession[] sessions, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(category) || category.Length > 64 || sessions.Length is 0 or > 12 ||
            sessions.Any(session => session is null || string.IsNullOrWhiteSpace(session.SessionType) || session.SessionType.Length > 64 ||
                session.Riders is null || session.Riders.Count > 1000 || session.Riders.Any(run => run is null ||
                    run.Name is null || run.Name.Length > 150 || run.UciId?.Length > 30 || run.Position is < 1 or > 10000 ||
                    run.State is not ("finished" or "pending" or "dnf" or "dns" or "dsq")))) return BadRequest();
        return Ok(await championship.ProjectAsync(roundId, category, sessions, cancellationToken));
    }
}
