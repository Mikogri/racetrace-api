using Microsoft.AspNetCore.Mvc;
using RaceTrace.Api.Features.Fantasy;
using RaceTrace.Api.Features.Fantasy.Import;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Controllers;

[ApiController]
[Route("api/admin/fantasy")]
[ServiceFilter(typeof(AdminAuthorizationFilter))]
public sealed class AdminFantasyController(PinkbikeFantasyImporter importer, FantasyService fantasy,
    FantasyPreparationService preparation, TimeProvider time) : ControllerBase
{
    [HttpPost("sync")]
    public async Task<IActionResult> Sync([FromQuery] bool prepare = true, CancellationToken token = default)
    {
        var round = await fantasy.NextRoundAsync(token);
        try
        {
            var snapshot = await importer.SyncAsync(time.GetUtcNow().Year, round?.String("id"), token);
            if (prepare && round is not null) await preparation.PrepareAsync(round, token);
            return Ok(new { snapshot.Id, snapshot.FetchedAtUtc, riderCount = snapshot.Riders.Count });
        }
        catch (HttpRequestException) { throw new ApiException(503, "FANTASY_SYNC_FAILED", "Pinkbike price synchronization failed. The last successful snapshot is preserved."); }
        catch (InvalidDataException) { throw new ApiException(422, "FANTASY_IMPORT_INVALID", "Pinkbike data failed validation. The last successful snapshot is preserved."); }
        catch (InvalidOperationException) { throw new ApiException(409, "FANTASY_SYNC_RUNNING", "A Fantasy price synchronization is already running."); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new ApiException(503, "FANTASY_SYNC_TIMEOUT", "Pinkbike price synchronization timed out."); }
    }

    [HttpPost("sync-html")]
    [Consumes("text/html")]
    [RequestSizeLimit(8_000_000)]
    public async Task<IActionResult> SyncHtml(CancellationToken token)
    {
        var round = await fantasy.NextRoundAsync(token);
        using var reader = new StreamReader(Request.Body);
        var html = await reader.ReadToEndAsync(token);
        try
        {
            var snapshot = await importer.ImportHtmlAsync(html, time.GetUtcNow().Year, round?.String("id"), "admin-saved-public-html", token);
            return Ok(new { snapshot.Id, snapshot.FetchedAtUtc, riderCount = snapshot.Riders.Count });
        }
        catch (InvalidDataException) { throw new ApiException(422, "FANTASY_IMPORT_INVALID", "The uploaded official athlete page failed validation."); }
        catch (InvalidOperationException) { throw new ApiException(409, "FANTASY_SYNC_RUNNING", "A Fantasy price synchronization is already running."); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new ApiException(503, "FANTASY_SYNC_TIMEOUT", "Fantasy price import timed out."); }
    }

    [HttpPost("prepare")]
    public async Task<IActionResult> Prepare(CancellationToken token)
    {
        var round = await fantasy.NextRoundAsync(token) ?? throw new ApiException(404, "NO_UPCOMING_ROUND", "No upcoming eligible round exists.");
        try { return Ok(await preparation.PrepareAsync(round, token)); }
        catch (InvalidDataException) { throw new ApiException(422, "FANTASY_PREPARATION_INVALID", "Race data or scoring could not be verified for a consistent Fantasy projection. The previous snapshot is preserved."); }
    }
}
