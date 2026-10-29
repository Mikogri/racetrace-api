using Microsoft.AspNetCore.Mvc;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Controllers;

[ApiController]
[Route("api/v1/filter-options")]
public sealed class FilterOptionsController(FilterOptionsService filters) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(FilterOptionsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) =>
        Ok(await filters.GetAsync(cancellationToken));
}
