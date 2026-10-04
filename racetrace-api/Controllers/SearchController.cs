using Microsoft.AspNetCore.Mvc;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Controllers;

[ApiController]
[Route("api/v1/search")]
public sealed class SearchController : ControllerBase
{
    private readonly SearchService _search;

    public SearchController(SearchService search)
    {
        _search = search;
    }

    [HttpGet]
    public async Task<IActionResult> Search(CancellationToken cancellationToken)
    {
        return Ok(await _search.SearchAsync(Request.Query, cancellationToken));
    }
}
