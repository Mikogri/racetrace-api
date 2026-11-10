using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Controllers;

[ApiController]
[Route("api/v1/home/ranking")]
public sealed class HomeRankingController(HomeRankingService ranking) : ControllerBase
{
    [HttpGet]
    [OutputCache(PolicyName = "ranking")]
    [ProducesResponseType(typeof(HomeRankingResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) =>
        Ok(await ranking.GetAsync(Request.Query, cancellationToken));
}
