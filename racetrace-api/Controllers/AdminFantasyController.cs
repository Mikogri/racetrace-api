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
public sealed class AdminFantasyController(
    PinkbikeFantasyImporter importer,
    FantasyService fantasy,
    FantasyPreparationService preparation,
    TimeProvider time
) : ControllerBase
{
    [HttpPost("sync")]
    [ProducesResponseType(typeof(System.Text.Json.Nodes.JsonObject), StatusCodes.Status200OK)]
    public async Task<IActionResult> Sync(
        [FromQuery] bool prepare = true,
        CancellationToken token = default,
        [FromHeader(Name = "Idempotency-Key")] string? runId = null
    )
    {
        var round = await fantasy.NextRoundAsync(token);
        try
        {
            var manualId = ManualRunId(runId);
            var snapshot = await importer.SyncAsync(
                time.GetUtcNow().Year,
                round?.String("id"),
                token,
                manualId
            );
            if (prepare && round is not null)
                await preparation.PrepareAsync(round, token, manualId);
            return Ok(
                new
                {
                    snapshot.Id,
                    snapshot.FetchedAtUtc,
                    riderCount = snapshot.Riders.Count,
                }
            );
        }
        catch (HttpRequestException)
        {
            throw new ApiException(
                503,
                "FANTASY_SYNC_FAILED",
                "Pinkbike price synchronization failed. The last successful snapshot is preserved."
            );
        }
        catch (InvalidDataException)
        {
            throw new ApiException(
                422,
                "FANTASY_IMPORT_INVALID",
                "Pinkbike data failed validation. The last successful snapshot is preserved."
            );
        }
        catch (InvalidOperationException)
        {
            throw new ApiException(
                409,
                "FANTASY_SYNC_RUNNING",
                "A Fantasy price synchronization is already running."
            );
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new ApiException(
                503,
                "FANTASY_SYNC_TIMEOUT",
                "Pinkbike price synchronization timed out."
            );
        }
    }

    [HttpPost("sync-html")]
    [Consumes("text/html")]
    [RequestSizeLimit(8_000_000)]
    [ProducesResponseType(typeof(System.Text.Json.Nodes.JsonObject), StatusCodes.Status200OK)]
    public async Task<IActionResult> SyncHtml(
        CancellationToken token,
        [FromHeader(Name = "Idempotency-Key")] string? runId = null
    )
    {
        var round = await fantasy.NextRoundAsync(token);
        using var reader = new StreamReader(Request.Body);
        var html = await reader.ReadToEndAsync(token);
        try
        {
            var snapshot = await importer.ImportHtmlAsync(
                html,
                time.GetUtcNow().Year,
                round?.String("id"),
                "admin-saved-public-html",
                token,
                ManualRunId(runId)
            );
            return Ok(
                new
                {
                    snapshot.Id,
                    snapshot.FetchedAtUtc,
                    riderCount = snapshot.Riders.Count,
                }
            );
        }
        catch (InvalidDataException)
        {
            throw new ApiException(
                422,
                "FANTASY_IMPORT_INVALID",
                "The uploaded official athlete page failed validation."
            );
        }
        catch (InvalidOperationException)
        {
            throw new ApiException(
                409,
                "FANTASY_SYNC_RUNNING",
                "A Fantasy price synchronization is already running."
            );
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new ApiException(503, "FANTASY_SYNC_TIMEOUT", "Fantasy price import timed out.");
        }
    }

    [HttpPost("prepare")]
    [ProducesResponseType(
        typeof(RaceTrace.Api.Features.Fantasy.FantasyPreparedRound),
        StatusCodes.Status200OK
    )]
    public async Task<IActionResult> Prepare(
        CancellationToken token,
        [FromHeader(Name = "Idempotency-Key")] string? runId = null
    )
    {
        var round =
            await fantasy.NextRoundAsync(token)
            ?? throw new ApiException(
                404,
                "NO_UPCOMING_ROUND",
                "No upcoming eligible round exists."
            );
        try
        {
            return Ok(await preparation.PrepareAsync(round, token, ManualRunId(runId)));
        }
        catch (InvalidDataException)
        {
            throw new ApiException(
                422,
                "FANTASY_PREPARATION_INVALID",
                "Race data or scoring could not be verified for a consistent Fantasy projection. The previous snapshot is preserved."
            );
        }
        catch (FantasyJobConflictException)
        {
            throw new ApiException(
                409,
                "FANTASY_SYNC_RUNNING",
                "Another worker is preparing this Fantasy round."
            );
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new ApiException(
                503,
                "FANTASY_PREPARATION_CANCELLED",
                "Fantasy preparation lost its lease or timed out. The previous snapshot is preserved."
            );
        }
    }

    private static string ManualRunId(string? key)
    {
        if (key is null)
            return "manual:" + Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(key) || key.Length > 180 || key.Any(char.IsControl))
            throw new ApiException(
                400,
                "INVALID_IDEMPOTENCY_KEY",
                "Idempotency-Key must contain 1 to 180 printable characters."
            );
        return "manual:" + key;
    }
}
