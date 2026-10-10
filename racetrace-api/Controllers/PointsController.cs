using Microsoft.AspNetCore.Mvc;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Controllers;

[ApiController]
public sealed class PointsController : ControllerBase
{
    private readonly PointsService _points;

    public PointsController(PointsService points)
    {
        _points = points;
    }

    [HttpGet("api/v1/points/systems")]
    [ProducesResponseType(typeof(RaceTrace.Api.Models.ListResponse<System.Text.Json.Nodes.JsonObject>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Systems(CancellationToken cancellationToken)
    {
        return Ok(await _points.ListSystemsAsync(Request.Query, cancellationToken));
    }

    [HttpGet("api/v1/competition-classes")]
    [ProducesResponseType(typeof(RaceTrace.Api.Models.ListResponse<System.Text.Json.Nodes.JsonObject>), StatusCodes.Status200OK)]
    public async Task<IActionResult> CompetitionClasses(CancellationToken cancellationToken)
    {
        return Ok(await _points.ListCompetitionClassesAsync(Request.Query, cancellationToken));
    }

    [HttpPost("api/admin/points/recalculate")]
    [ServiceFilter(typeof(AdminAuthorizationFilter))]
    [ProducesResponseType(typeof(System.Text.Json.Nodes.JsonObject), StatusCodes.Status200OK)]
    public async Task<IActionResult> Recalculate([FromBody] PointsRecalculateRequest request, CancellationToken cancellationToken)
    {
        return Ok(await _points.RecalculateAsync(request, cancellationToken));
    }

    [HttpPost("api/admin/preaggregations/refresh")]
    [ServiceFilter(typeof(AdminAuthorizationFilter))]
    [ProducesResponseType(typeof(System.Text.Json.Nodes.JsonObject), StatusCodes.Status200OK)]
    public async Task<IActionResult> RefreshPreaggregations(
        [FromBody] RankingPreaggregationRefreshRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await _points.RefreshRankingPreaggregationsAsync(request, cancellationToken));
    }
}
