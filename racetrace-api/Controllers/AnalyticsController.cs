using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Controllers;

[ApiController]
[Route("api/v1/analytics")]
public sealed class AnalyticsController : ControllerBase
{
    private readonly AnalyticsService _analytics;

    public AnalyticsController(AnalyticsService analytics)
    {
        _analytics = analytics;
    }

    [HttpGet("round-profile")]
    [OutputCache(PolicyName = "analytics")]
    [ProducesResponseType(typeof(RaceTrace.Api.Models.RoundProfileResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> RoundProfile(CancellationToken cancellationToken)
    {
        return Ok(await _analytics.GetRoundProfileAsync(Request.Query, cancellationToken));
    }

    [HttpGet("riders/{riderId}/pressure-performance")]
    [OutputCache(PolicyName = "analytics")]
    [ProducesResponseType(typeof(RaceTrace.Api.Models.PressurePerformanceResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> PressurePerformance(
        string riderId,
        CancellationToken cancellationToken)
    {
        return Ok(await _analytics.GetPressurePerformanceAsync(
            riderId,
            Request.Query,
            cancellationToken));
    }
}
