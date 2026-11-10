using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Controllers;

[ApiController]
[Route("api/v1/seasons")]
public sealed class SeasonsController : ControllerBase
{
    private readonly SeasonSummaryService _seasons;

    public SeasonsController(SeasonSummaryService seasons)
    {
        _seasons = seasons;
    }

    [HttpGet("{season:int}/overview")]
    [OutputCache(PolicyName = "season-overview")]
    [ProducesResponseType(
        typeof(RaceTrace.Api.Models.SeasonOverviewResponseDto),
        StatusCodes.Status200OK
    )]
    public async Task<IActionResult> Overview(int season, CancellationToken cancellationToken)
    {
        return Ok(await _seasons.GetOverviewAsync(season, cancellationToken));
    }
}
