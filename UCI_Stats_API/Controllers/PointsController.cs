using Microsoft.AspNetCore.Mvc;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Models;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Controllers;

[ApiController]
public sealed class PointsController : ControllerBase
{
    private readonly PointsService _points;

    public PointsController(PointsService points)
    {
        _points = points;
    }

    [HttpGet("api/v1/points/systems")]
    public async Task<IActionResult> Systems(CancellationToken cancellationToken)
    {
        return Ok(await _points.ListSystemsAsync(Request.Query, cancellationToken));
    }

    [HttpGet("api/v1/competition-classes")]
    public async Task<IActionResult> CompetitionClasses(CancellationToken cancellationToken)
    {
        return Ok(await _points.ListCompetitionClassesAsync(Request.Query, cancellationToken));
    }

    [HttpPost("api/admin/points/recalculate")]
    [ServiceFilter(typeof(AdminAuthorizationFilter))]
    public async Task<IActionResult> Recalculate([FromBody] PointsRecalculateRequest request, CancellationToken cancellationToken)
    {
        return Ok(await _points.RecalculateAsync(request, cancellationToken));
    }

    [HttpPost("api/admin/preaggregations/refresh")]
    [ServiceFilter(typeof(AdminAuthorizationFilter))]
    public async Task<IActionResult> RefreshPreaggregations(
        [FromBody] RankingPreaggregationRefreshRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await _points.RefreshRankingPreaggregationsAsync(request, cancellationToken));
    }
}
