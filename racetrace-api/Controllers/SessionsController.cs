using Microsoft.AspNetCore.Mvc;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Controllers;

[ApiController]
[Route("api/v1/sessions")]
public sealed class SessionsController : ControllerBase
{
    private readonly SessionsService _sessions;

    public SessionsController(SessionsService sessions)
    {
        _sessions = sessions;
    }

    [HttpGet("common")]
    [ProducesResponseType(typeof(RaceTrace.Api.Models.PublicPagedResponse<RaceTrace.Api.Models.CommonSessionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Common(CancellationToken cancellationToken)
    {
        return Ok(await _sessions.ListCommonSessionsAsync(Request.Query, cancellationToken));
    }
}
