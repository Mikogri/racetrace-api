using Microsoft.AspNetCore.Mvc;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Controllers;

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
    [ProducesResponseType(
        typeof(RaceTrace.Api.Models.PublicPagedResponse<RaceTrace.Api.Models.TrackFilterOptionDto>),
        StatusCodes.Status200OK
    )]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        return Ok(await _tracks.ListTracksAsync(Request.Query, cancellationToken));
    }

    [HttpGet("{trackId}")]
    [ProducesResponseType(typeof(System.Text.Json.Nodes.JsonObject), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(string trackId, CancellationToken cancellationToken)
    {
        return Ok(
            await _tracks.GetTrackAsync(
                trackId,
                _validator.Csv(Request.Query, "include"),
                Request.Query,
                cancellationToken
            )
        );
    }

    [HttpGet("{trackId}/records")]
    [ProducesResponseType(
        typeof(RaceTrace.Api.Models.ListResponse<System.Text.Json.Nodes.JsonObject>),
        StatusCodes.Status200OK
    )]
    public async Task<IActionResult> Records(string trackId, CancellationToken cancellationToken)
    {
        return Ok(await _tracks.BuildRecordsAsync(trackId, Request.Query, cancellationToken));
    }
}
