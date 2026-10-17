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
    [ProducesResponseType(typeof(System.Text.Json.Nodes.JsonObject), StatusCodes.Status200OK)]
    public IActionResult EditableResources()
    {
        return Ok(AdminJsonFields.EditableResources(_adminData.EditableResources()));
    }

    [HttpPatch("riders/{riderId:long}")]
    [ProducesResponseType(typeof(System.Text.Json.Nodes.JsonObject), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateRider(
        string riderId,
        [FromBody] JsonObject? patch,
        CancellationToken cancellationToken)
    {
        return Ok(await _adminData.UpdateRiderAsync(riderId, AdminJsonFields.ToDatabasePatch(patch), cancellationToken));
    }

    [HttpPatch("{resource}/{id:long}")]
    [ProducesResponseType(typeof(System.Text.Json.Nodes.JsonObject), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(
        string resource,
        string id,
        [FromBody] JsonObject? patch,
        CancellationToken cancellationToken)
    {
        return Ok(await _adminData.UpdateAsync(resource, id, AdminJsonFields.ToDatabasePatch(patch), cancellationToken));
    }

    [HttpPatch("rounds/{roundId:long}/track")]
    [ProducesResponseType(typeof(System.Text.Json.Nodes.JsonObject), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateRoundTrack(
        string roundId,
        [FromBody] JsonObject? patch,
        CancellationToken cancellationToken)
    {
        return Ok(await _adminData.UpdateRoundTrackAsync(roundId, AdminJsonFields.ToDatabasePatch(patch), cancellationToken));
    }

    [HttpPut("rounds/{roundId:long}/image")]
    [ProducesResponseType(typeof(System.Text.Json.Nodes.JsonObject), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpsertRoundImage(
        string roundId,
        [FromBody] ImportRoundImagePayload payload,
        CancellationToken cancellationToken)
    {
        return Ok(await _adminData.UpsertRoundImageAsync(roundId, payload, cancellationToken));
    }

    [HttpPost("round-images/convert-webp")]
    [ProducesResponseType(typeof(JsonObject), StatusCodes.Status200OK)]
    public async Task<IActionResult> ConvertRoundImagesToWebp(
        [FromServices] RoundImageBackfillService backfill,
        [FromQuery] int limit = 10,
        CancellationToken cancellationToken = default)
    {
        return Ok(await backfill.ConvertBatchAsync(limit, cancellationToken));
    }

    [HttpDelete("rounds/{roundId:long}")]
    [ProducesResponseType(typeof(System.Text.Json.Nodes.JsonObject), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteRound(
        string roundId,
        CancellationToken cancellationToken)
    {
        return Ok(await _adminData.DeleteRoundAsync(roundId, cancellationToken));
    }

    [HttpPost("sessions/{sessionId:long}/reimport")]
    [ProducesResponseType(typeof(RaceTrace.Api.Models.ChronoRaceSessionReimportResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> ReimportSession(
        string sessionId,
        [FromBody] ChronoRaceSessionReimportRequest? request,
        CancellationToken cancellationToken)
    {
        return Ok(await _sessionReimport.ReimportAsync(sessionId, request, cancellationToken));
    }

    [HttpPost("sessions/{sessionId:long}/supplement/uci-mtb-world-series")]
    [ProducesResponseType(typeof(RaceTrace.Api.Models.UciMtbWorldSeriesSupplementResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> SupplementUciMtbWorldSeriesResults(
        string sessionId,
        [FromBody] JsonObject? payload,
        CancellationToken cancellationToken)
    {
        return Ok(await _resultsSupplement.SupplementAsync(sessionId, payload, cancellationToken));
    }
}
