using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Controllers;

[ApiController]
[Route("api/admin")]
[ServiceFilter(typeof(AdminAuthorizationFilter))]
public sealed class AdminDataController : ControllerBase
{
    private readonly AdminDataService _adminData;
    private readonly AdminSessionReimportService _sessionReimport;
    private readonly UciMtbWorldSeriesSupplementService _resultsSupplement;

    public AdminDataController(
        AdminDataService adminData,
        AdminSessionReimportService sessionReimport,
        UciMtbWorldSeriesSupplementService resultsSupplement)
    {
        _adminData = adminData;
        _sessionReimport = sessionReimport;
        _resultsSupplement = resultsSupplement;
    }

    [HttpGet("editable-resources")]
    public IActionResult EditableResources()
    {
        return Ok(_adminData.EditableResources());
    }

    [HttpPatch("riders/{riderId:long}")]
    public async Task<IActionResult> UpdateRider(
        string riderId,
        [FromBody] JsonObject? patch,
        CancellationToken cancellationToken)
    {
        return Ok(await _adminData.UpdateRiderAsync(riderId, patch, cancellationToken));
    }

    [HttpPatch("{resource}/{id:long}")]
    public async Task<IActionResult> Update(
        string resource,
        string id,
        [FromBody] JsonObject? patch,
        CancellationToken cancellationToken)
    {
        return Ok(await _adminData.UpdateAsync(resource, id, patch, cancellationToken));
    }

    [HttpPatch("rounds/{roundId:long}/track")]
    public async Task<IActionResult> UpdateRoundTrack(
        string roundId,
        [FromBody] JsonObject? patch,
        CancellationToken cancellationToken)
    {
        return Ok(await _adminData.UpdateRoundTrackAsync(roundId, patch, cancellationToken));
    }

    [HttpPut("rounds/{roundId:long}/image")]
    public async Task<IActionResult> UpsertRoundImage(
        string roundId,
        [FromBody] ImportRoundImagePayload payload,
        CancellationToken cancellationToken)
    {
        return Ok(await _adminData.UpsertRoundImageAsync(roundId, payload, cancellationToken));
    }

    [HttpDelete("rounds/{roundId:long}")]
    public async Task<IActionResult> DeleteRound(
        string roundId,
        CancellationToken cancellationToken)
    {
        return Ok(await _adminData.DeleteRoundAsync(roundId, cancellationToken));
    }

    [HttpPost("sessions/{sessionId:long}/reimport")]
    public async Task<IActionResult> ReimportSession(
        string sessionId,
        [FromBody] ChronoRaceSessionReimportRequest? request,
        CancellationToken cancellationToken)
    {
        return Ok(await _sessionReimport.ReimportAsync(sessionId, request, cancellationToken));
    }

    [HttpPost("sessions/{sessionId:long}/supplement/uci-mtb-world-series")]
    public async Task<IActionResult> SupplementUciMtbWorldSeriesResults(
        string sessionId,
        [FromBody] JsonObject? payload,
        CancellationToken cancellationToken)
    {
        return Ok(await _resultsSupplement.SupplementAsync(sessionId, payload, cancellationToken));
    }
}
