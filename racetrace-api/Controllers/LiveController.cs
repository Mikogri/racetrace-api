using Microsoft.AspNetCore.Mvc;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Controllers;

[ApiController]
[Route("api/v1/live/sessions")]
public sealed class LiveController : ControllerBase
{
    private readonly LiveService _live;
    private readonly QueryValidationService _validator;

    public LiveController(LiveService live, QueryValidationService validator)
    {
        _live = live;
        _validator = validator;
    }

    [HttpGet]
    [ProducesResponseType(typeof(RaceTrace.Api.Models.ListResponse<System.Text.Json.Nodes.JsonObject>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Sessions(CancellationToken cancellationToken)
    {
        return Ok(await _live.ListLiveSessionsAsync(Request.Query, cancellationToken));
    }

    [HttpGet("{sessionId}")]
    [ProducesResponseType(typeof(System.Text.Json.Nodes.JsonObject), StatusCodes.Status200OK)]
    public async Task<IActionResult> Session(string sessionId, CancellationToken cancellationToken)
    {
        return Ok(await _live.GetLiveSessionAsync(sessionId, _validator.Csv(Request.Query, "include"), cancellationToken));
    }

    [HttpGet("{sessionId}/leaderboard")]
    [ProducesResponseType(typeof(RaceTrace.Api.Models.ListResponse<System.Text.Json.Nodes.JsonObject>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Leaderboard(string sessionId, CancellationToken cancellationToken)
    {
        return Ok(await _live.GetLeaderboardAsync(sessionId, Request.Query, cancellationToken));
    }
}
