using Microsoft.AspNetCore.Mvc;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Controllers;

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
        QueryValidationService validator)
    {
        _riders = riders;
        _ranking = ranking;
        _validator = validator;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        return Ok(await _ranking.ListRankingAsync(Request.Query, cancellationToken));
    }

    [HttpGet("{riderId}")]
    public async Task<IActionResult> Get(string riderId, CancellationToken cancellationToken)
    {
        return Ok(await _riders.GetRiderAsync(riderId, _validator.Csv(Request.Query, "include"), Request.Query, cancellationToken));
    }

    [HttpGet("{riderId}/metrics")]
    public async Task<IActionResult> Metrics(string riderId, CancellationToken cancellationToken)
    {
        return Ok(await _riders.GetPublicStatisticsAsync(riderId, Request.Query, cancellationToken));
    }

    [HttpGet("{riderId}/overview")]
    public async Task<IActionResult> Overview(string riderId, CancellationToken cancellationToken)
    {
        return Ok(await _riders.GetOverviewAsync(riderId, Request.Query, cancellationToken));
    }

    [HttpGet("{riderId}/results")]
    public async Task<IActionResult> Results(string riderId, CancellationToken cancellationToken)
    {
        return Ok(await _riders.ListPublicRiderRoundResultsAsync(riderId, Request.Query, cancellationToken));
    }

    [HttpGet("{riderId}/venues")]
    public async Task<IActionResult> Venues(string riderId, CancellationToken cancellationToken)
    {
        return Ok(await _riders.ListPublicTrackStatisticsAsync(riderId, Request.Query, cancellationToken));
    }

    [HttpGet("{riderId}/team-history")]
    public async Task<IActionResult> TeamHistory(
        string riderId,
        CancellationToken cancellationToken)
    {
        return Ok(await _riders.GetTeamHistoryAsync(
            riderId,
            Request.Query,
            cancellationToken));
    }
}
