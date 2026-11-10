using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Controllers;

[ApiController]
[Route("api/v1/live/chronorace")]
public sealed class ChronoRaceLiveController : ControllerBase
{
    private static readonly JsonSerializerOptions ProviderJson = new(JsonSerializerDefaults.Web);
    private readonly ChronoRaceLiveProxyService _chronorace;

    public ChronoRaceLiveController(ChronoRaceLiveProxyService chronorace)
    {
        _chronorace = chronorace;
    }

    [HttpGet("catalog/{context}")]
    [ProducesResponseType(typeof(System.Text.Json.Nodes.JsonNode), StatusCodes.Status200OK)]
    public async Task<IActionResult> Catalog(string context, CancellationToken cancellationToken) =>
        ProviderPayload(await _chronorace.GetCatalogAsync(context, cancellationToken));

    [HttpGet("results/{context}/{key}")]
    [ProducesResponseType(typeof(System.Text.Json.Nodes.JsonNode), StatusCodes.Status200OK)]
    public async Task<IActionResult> Results(
        string context,
        string key,
        CancellationToken cancellationToken
    ) => ProviderPayload(await _chronorace.GetResultsAsync(context, key, cancellationToken));

    // This proxy exposes the provider's document, not a RaceTrace DTO.
    private IActionResult ProviderPayload(JsonNode? payload) =>
        payload is null ? NoContent() : new JsonResult(payload, ProviderJson);
}
