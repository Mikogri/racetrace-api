using Microsoft.AspNetCore.Mvc;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Controllers;

[ApiController]
public sealed class ResultsController : ControllerBase
{
    private readonly ResultsService _results;
    private readonly QueryValidationService _validator;

    public ResultsController(ResultsService results, QueryValidationService validator)
    {
        _results = results;
        _validator = validator;
    }

    [HttpGet("api/v1/sessions/{sessionId}/results")]
    [ProducesResponseType(
        typeof(RaceTrace.Api.Models.SessionResultsResponseDto),
        StatusCodes.Status200OK
    )]
    public async Task<IActionResult> SessionResults(
        string sessionId,
        CancellationToken cancellationToken
    )
    {
        return Ok(
            await _results.ListPublicSessionResultsAsync(
                sessionId,
                Request.Query,
                cancellationToken
            )
        );
    }

    [HttpGet("api/v1/results/{resultId}")]
    [ProducesResponseType(typeof(System.Text.Json.Nodes.JsonObject), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(string resultId, CancellationToken cancellationToken)
    {
        return Ok(
            await _results.GetResultAsync(
                resultId,
                _validator.Csv(Request.Query, "include"),
                cancellationToken
            )
        );
    }

    [HttpGet("api/v1/results/{resultId}/time-splits")]
    [ProducesResponseType(
        typeof(RaceTrace.Api.Models.ListResponse<System.Text.Json.Nodes.JsonObject>),
        StatusCodes.Status200OK
    )]
    public async Task<IActionResult> TimeSplits(
        string resultId,
        CancellationToken cancellationToken
    )
    {
        return Ok(await _results.ListTimeSplitsAsync(resultId, Request.Query, cancellationToken));
    }
}
