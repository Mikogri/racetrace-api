using Microsoft.AspNetCore.Mvc;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Controllers;

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
    public async Task<IActionResult> SessionResults(string sessionId, CancellationToken cancellationToken)
    {
        return Ok(await _results.ListPublicSessionResultsAsync(sessionId, Request.Query, cancellationToken));
    }

    [HttpGet("api/v1/results/{resultId}")]
    public async Task<IActionResult> Get(string resultId, CancellationToken cancellationToken)
    {
        return Ok(await _results.GetResultAsync(resultId, _validator.Csv(Request.Query, "include"), cancellationToken));
    }

    [HttpGet("api/v1/results/{resultId}/time-splits")]
    public async Task<IActionResult> TimeSplits(string resultId, CancellationToken cancellationToken)
    {
        return Ok(await _results.ListTimeSplitsAsync(resultId, Request.Query, cancellationToken));
    }
}
