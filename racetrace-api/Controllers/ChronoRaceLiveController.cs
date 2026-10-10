using Microsoft.AspNetCore.Mvc;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Controllers;

[ApiController]
[Route("api/v1/live/chronorace")]
public sealed class ChronoRaceLiveController : ControllerBase
{
    private readonly ChronoRaceLiveProxyService _chronorace;

    public ChronoRaceLiveController(ChronoRaceLiveProxyService chronorace)
    {
        _chronorace = chronorace;
    }

    [HttpGet("catalog/{context}")]
    [ProducesResponseType(typeof(System.Text.Json.Nodes.JsonNode), StatusCodes.Status200OK)]
    public async Task<IActionResult> Catalog(string context, CancellationToken cancellationToken) =>
        Ok(await _chronorace.GetCatalogAsync(context, cancellationToken));

    [HttpGet("results/{context}/{key}")]
    [ProducesResponseType(typeof(System.Text.Json.Nodes.JsonNode), StatusCodes.Status200OK)]
    public async Task<IActionResult> Results(string context, string key, CancellationToken cancellationToken) =>
        Ok(await _chronorace.GetResultsAsync(context, key, cancellationToken));
}
