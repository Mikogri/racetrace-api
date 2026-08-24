using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Controllers;

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
    public async Task<IActionResult> Overview(
        int season,
        CancellationToken cancellationToken)
    {
        return Ok(await _seasons.GetOverviewAsync(season, cancellationToken));
    }
}
