using Microsoft.AspNetCore.Mvc;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Controllers;

[ApiController]
[Route("api/v1/riders")]
public sealed class RidersController : ControllerBase
{
    private readonly RidersService _riders;
    private readonly RankingService _ranking;
    private readonly QueryValidationService _validator;

    public RidersController(
        RidersService riders,
        RankingService ranking,
        QueryValidationService validator
    )
    {
        _riders = riders;
        _ranking = ranking;
        _validator = validator;
    }

    [HttpGet]
    [ProducesResponseType(typeof(RaceTrace.Api.Models.RankingResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        return Ok(await _ranking.ListRankingAsync(Request.Query, cancellationToken));
    }

    [HttpGet("resolve")]
    [ProducesResponseType(typeof(System.Text.Json.Nodes.JsonObject), StatusCodes.Status200OK)]
    public async Task<IActionResult> Resolve(CancellationToken cancellationToken)
    {
        return Ok(
            await _riders.ResolveUciIdsAsync(
                _validator.Csv(Request.Query, "uciIds"),
                cancellationToken
            )
        );
    }

    [HttpGet("{riderId}")]
    [ProducesResponseType(typeof(System.Text.Json.Nodes.JsonObject), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(string riderId, CancellationToken cancellationToken)
    {
        return Ok(
            await _riders.GetRiderAsync(
                riderId,
                _validator.Csv(Request.Query, "include"),
                Request.Query,
                cancellationToken
            )
        );
    }

    [HttpGet("{riderId}/metrics")]
    [ProducesResponseType(typeof(RaceTrace.Api.Models.RiderStatisticsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Metrics(string riderId, CancellationToken cancellationToken)
    {
        return Ok(
            await _riders.GetPublicStatisticsAsync(riderId, Request.Query, cancellationToken)
        );
    }

    [HttpGet("{riderId}/overview")]
    [ProducesResponseType(typeof(RaceTrace.Api.Models.RiderOverviewDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Overview(string riderId, CancellationToken cancellationToken)
    {
        return Ok(await _riders.GetOverviewAsync(riderId, Request.Query, cancellationToken));
    }

    [HttpGet("{riderId}/results")]
    [ProducesResponseType(
        typeof(RaceTrace.Api.Models.PublicPagedResponse<RaceTrace.Api.Models.RiderRoundResultDto>),
        StatusCodes.Status200OK
    )]
    public async Task<IActionResult> Results(string riderId, CancellationToken cancellationToken)
    {
        return Ok(
            await _riders.ListPublicRiderRoundResultsAsync(
                riderId,
                Request.Query,
                cancellationToken
            )
        );
    }

    [HttpGet("{riderId}/venues")]
    [ProducesResponseType(
        typeof(RaceTrace.Api.Models.PublicPagedResponse<RaceTrace.Api.Models.RiderTrackStatisticsDto>),
        StatusCodes.Status200OK
    )]
    public async Task<IActionResult> Venues(string riderId, CancellationToken cancellationToken)
    {
        return Ok(
            await _riders.ListPublicTrackStatisticsAsync(riderId, Request.Query, cancellationToken)
        );
    }

    [HttpGet("{riderId}/team-history")]
    [ProducesResponseType(
        typeof(IReadOnlyList<RaceTrace.Api.Models.RiderTeamHistoryDto>),
        StatusCodes.Status200OK
    )]
    public async Task<IActionResult> TeamHistory(
        string riderId,
        CancellationToken cancellationToken
    )
    {
        return Ok(await _riders.GetTeamHistoryAsync(riderId, Request.Query, cancellationToken));
    }
}
