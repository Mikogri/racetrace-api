using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Controllers;

[ApiController]
[Route("api/v1/podium-predictor")]
public sealed class PodiumPredictorController : ControllerBase
{
    private readonly PodiumPredictorService _predictor;

    public PodiumPredictorController(PodiumPredictorService predictor)
    {
        _predictor = predictor;
    }

    [HttpGet]
    [OutputCache(PolicyName = "season-overview")]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        return Ok(await _predictor.GetAsync(Request.Query, cancellationToken));
    }
}
