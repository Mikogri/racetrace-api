using Microsoft.AspNetCore.Mvc;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Controllers;

[ApiController]
[Route("api/v1/teams")]
public sealed class TeamsController : ControllerBase
{
    private readonly TeamsService _teams;

    public TeamsController(TeamsService teams)
    {
        _teams = teams;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        return Ok(await _teams.ListTeamsAsync(Request.Query, cancellationToken));
    }

    [HttpGet("{teamId}")]
    public async Task<IActionResult> Get(string teamId, CancellationToken cancellationToken)
    {
        return Ok(await _teams.GetTeamAsync(teamId, cancellationToken));
    }
}
