using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Controllers;

[ApiController]
[Route("api/v1/ranking")]
public sealed class RankingController : ControllerBase
{
    private readonly RankingService _ranking;

    public RankingController(RankingService ranking)
    {
        _ranking = ranking;
    }

    [HttpGet]
    [OutputCache(PolicyName = "ranking")]
    [ProducesResponseType(typeof(RaceTrace.Api.Models.RankingResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var response = await _ranking.ListRankingAsync(Request.Query, cancellationToken);
        return Ok(response);
    }
}
