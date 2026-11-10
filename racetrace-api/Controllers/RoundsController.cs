using Microsoft.AspNetCore.Mvc;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Controllers;

[ApiController]
[Route("api/v1/rounds")]
public sealed class RoundsController : ControllerBase
{
    private readonly RoundsService _rounds;
    private readonly QueryValidationService _validator;
    private readonly ResultsService? _results;

    public RoundsController(
        RoundsService rounds,
        QueryValidationService validator,
        ResultsService? results = null
    )
    {
        _rounds = rounds;
        _validator = validator;
        _results = results;
    }

    [HttpGet]
    [ProducesResponseType(
        typeof(RaceTrace.Api.Models.PublicPagedResponse<RaceTrace.Api.Models.RoundListItemDto>),
        StatusCodes.Status200OK
    )]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        return Ok(await _rounds.ListRoundsAsync(Request.Query, cancellationToken));
    }

    [HttpGet("seasons")]
    [ProducesResponseType(typeof(System.Text.Json.Nodes.JsonObject), StatusCodes.Status200OK)]
    public async Task<IActionResult> Seasons(CancellationToken cancellationToken)
    {
        return Ok(new { items = await _rounds.ListSeasonsAsync(cancellationToken) });
    }

    [HttpGet("{roundId}")]
    [ProducesResponseType(typeof(System.Text.Json.Nodes.JsonObject), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(string roundId, CancellationToken cancellationToken)
    {
        return Ok(
            await _rounds.GetRoundAsync(
                roundId,
                _validator.Csv(Request.Query, "include"),
                cancellationToken
            )
        );
    }

    [HttpGet("{roundId:long}/image")]
    [ProducesResponseType(typeof(byte[]), StatusCodes.Status200OK)]
    public async Task<IActionResult> Image(long roundId, CancellationToken cancellationToken)
    {
        var image = await _rounds.GetRoundImageAsync(roundId, cancellationToken);
        if (image is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "public, max-age=86400";
        if (!string.IsNullOrWhiteSpace(image.StorageUrl))
        {
            return Redirect(image.StorageUrl);
        }

        return File(image.Bytes!, image.ContentType);
    }

    [HttpGet("{roundId}/sessions")]
    [ProducesResponseType(
        typeof(RaceTrace.Api.Models.PublicPagedResponse<RaceTrace.Api.Models.CommonSessionDto>),
        StatusCodes.Status200OK
    )]
    public async Task<IActionResult> Sessions(string roundId, CancellationToken cancellationToken)
    {
        return Ok(await _rounds.ListRoundSessionsAsync(roundId, Request.Query, cancellationToken));
    }

    [HttpGet("{roundId}/results")]
    [ProducesResponseType(
        typeof(RaceTrace.Api.Models.SessionResultsResponseDto),
        StatusCodes.Status200OK
    )]
    public async Task<IActionResult> Results(string roundId, CancellationToken cancellationToken)
    {
        return Ok(
            await (
                _results ?? throw new InvalidOperationException("ResultsService is unavailable.")
            ).ListPublicRoundResultsAsync(roundId, Request.Query, cancellationToken)
        );
    }
}
