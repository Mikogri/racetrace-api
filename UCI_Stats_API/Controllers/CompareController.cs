using Microsoft.AspNetCore.Mvc;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Controllers;

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
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        return Ok(await _compare.CompareAsync(Request.Query, cancellationToken));
    }
}
