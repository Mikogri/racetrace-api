using Microsoft.AspNetCore.Mvc;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Controllers;

[ApiController]
[Route("api/v1/tracks")]
public sealed class TracksController : ControllerBase
{
    private readonly TracksService _tracks;
    private readonly QueryValidationService _validator;

    public TracksController(TracksService tracks, QueryValidationService validator)
    {
        _tracks = tracks;
        _validator = validator;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        return Ok(await _tracks.ListTracksAsync(Request.Query, cancellationToken));
    }

    [HttpGet("{trackId}")]
    public async Task<IActionResult> Get(string trackId, CancellationToken cancellationToken)
    {
        return Ok(await _tracks.GetTrackAsync(trackId, _validator.Csv(Request.Query, "include"), Request.Query, cancellationToken));
    }

    [HttpGet("{trackId}/records")]
    public async Task<IActionResult> Records(string trackId, CancellationToken cancellationToken)
    {
        return Ok(await _tracks.BuildRecordsAsync(trackId, Request.Query, cancellationToken));
    }
}
