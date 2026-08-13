using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Controllers;

[ApiController]
[Route("api/v1/season-summary")]
public sealed class SeasonSummaryController : ControllerBase
{
    private readonly SeasonSummaryService _seasonSummary;

    public SeasonSummaryController(SeasonSummaryService seasonSummary)
    {
        _seasonSummary = seasonSummary;
    }

    [HttpGet]
    [OutputCache(PolicyName = "season-summary")]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        return Ok(await _seasonSummary.GetAsync(Request.Query, cancellationToken));
    }
}
