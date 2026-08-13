using Microsoft.AspNetCore.Mvc;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Controllers;

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
    public async Task<IActionResult> Common(CancellationToken cancellationToken)
    {
        return Ok(await _sessions.ListCommonSessionsAsync(Request.Query, cancellationToken));
    }
}
