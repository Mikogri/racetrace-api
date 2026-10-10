using Microsoft.AspNetCore.Mvc;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Controllers;

[ApiController]
[Route("api/v1/compare")]
public sealed class CompareController : ControllerBase
{
    private readonly CompareService _compare;

    public CompareController(CompareService compare)
    {
        _compare = compare;
    }

    [HttpGet]
    [ProducesResponseType(typeof(RaceTrace.Api.Models.CompareResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        return Ok(await _compare.CompareAsync(Request.Query, cancellationToken));
    }
}
