using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Controllers;

[ApiController]
[Route("api/v1/analytics")]
public sealed class AnalyticsController : ControllerBase
{
    private readonly RiderSimilarityService _riderSimilarity;
    private readonly AnalyticsService _analytics;

    public AnalyticsController(
        RiderSimilarityService riderSimilarity,
        AnalyticsService analytics)
    {
        _riderSimilarity = riderSimilarity;
        _analytics = analytics;
    }

    /// <summary>
    /// Returns normalized sector-performance profiles, pairwise rider similarity,
    /// flat clusters and hierarchical merge data for a dendrogram.
    /// </summary>
    /// <remarks>
    /// Similarity is normalized to 0-1. A value of 1 means nearly identical
    /// sector characteristics. Lower values indicate increasingly different profiles.
    /// Evidence describes the amount of sector and session data behind each profile.
    /// </remarks>
    [HttpGet("rider-similarity")]
    [OutputCache(PolicyName = "analytics")]
    public async Task<IActionResult> RiderSimilarity(CancellationToken cancellationToken)
    {
        return Ok(await _riderSimilarity.GetAsync(Request.Query, cancellationToken));
    }

    [HttpGet("round-profile")]
    [OutputCache(PolicyName = "analytics")]
    public async Task<IActionResult> RoundProfile(CancellationToken cancellationToken)
    {
        return Ok(await _analytics.GetRoundProfileAsync(Request.Query, cancellationToken));
    }

    [HttpGet("riders/{riderId}/pressure-performance")]
    [OutputCache(PolicyName = "analytics")]
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
