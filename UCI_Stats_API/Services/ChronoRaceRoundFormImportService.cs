using System.Text.Json.Nodes;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Models;

namespace UCI_Stats_API.Services;

public sealed class ChronoRaceRoundFormImportService
{
    private readonly ChronoRaceJsonFetcher _jsonFetcher;
    private readonly TissotTimingJsonFetcher _tissotJsonFetcher;
    private readonly ChronoRaceImportService _sessionImport;
    private readonly ISupabaseRestClient _supabase;

    public ChronoRaceRoundFormImportService(
        ChronoRaceJsonFetcher jsonFetcher,
        TissotTimingJsonFetcher tissotJsonFetcher,
        ChronoRaceImportService sessionImport,
        ISupabaseRestClient supabase)
    {
        _jsonFetcher = jsonFetcher;
        _tissotJsonFetcher = tissotJsonFetcher;
        _sessionImport = sessionImport;
        _supabase = supabase;
    }

    public async Task<ChronoRaceRoundFormImportResponse> ImportAsync(ChronoRaceRoundFormImportRequest request, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var image = RoundImagePayloadParser.Parse(request.Image);

        var pendingImports = new List<PendingChronoRaceImport>();
        foreach (var session in request.Sessions)
        {
            foreach (var category in session.Categories)
            {
                var json = category.ResultJson ?? category.ChronoraceJson;
                string? resolvedUrl = null;
                var fetchWarnings = new List<string>();
                if (json is null)
                {
                    if (!string.IsNullOrWhiteSpace(category.ResultUrl))
                    {
                        var fetched = await _tissotJsonFetcher.FetchAsync(category.ResultUrl, cancellationToken);
                        json = fetched.Json;
                        resolvedUrl = fetched.ResolvedUrl;
                    }
                    else
                    {
                        var fetched = await _jsonFetcher.FetchAsync(category.ChronoraceUrl!, cancellationToken);
                        json = fetched.Json;
                        resolvedUrl = fetched.ResolvedUrl;
                        fetchWarnings.AddRange(fetched.Warnings);
                    }
                }
                else if (!ImportResultJsonNormalizer.IsSupported(json))
                {
                    throw new ApiException(
                        StatusCodes.Status400BadRequest,
                        "INVALID_RESULT_JSON",
                        $"result_json for session '{session.Name}' and category '{category.CategoryCode}' is not a supported ChronoRace or UCI World Championship result document.");
                }

                pendingImports.Add(new PendingChronoRaceImport(session, category, json, resolvedUrl, fetchWarnings));
            }
        }

        var items = new List<ChronoRaceRoundFormImportItemResponse>();
        var allWarnings = new List<string>();
        foreach (var pending in pendingImports)
        {
            var sessionPayload = new ImportSessionPayload(
                pending.Session.Name,
                pending.Session.SessionType,
                pending.Session.SessionDate,
                pending.Session.Status);

            var importRequest = new ChronoRaceImportRequest(
                request.Round,
                request.Track,
                request.CompetitionClass,
                pending.Session.Condition ?? request.Condition,
                pending.Category.CategoryCode,
                sessionPayload,
                pending.Json);

            var result = await _sessionImport.ImportSessionAsync(importRequest, cancellationToken);
            var itemWarnings = pending.FetchWarnings
                .Concat(result.Warnings)
                .ToArray();

            foreach (var warning in itemWarnings)
            {
                allWarnings.Add($"{pending.Session.Name}/{pending.Category.CategoryCode}: {warning}");
            }

            items.Add(new ChronoRaceRoundFormImportItemResponse(
                pending.Session.Name,
                pending.Session.SessionType,
                pending.Category.CategoryCode,
                pending.Category.ResultUrl ?? pending.Category.ChronoraceUrl,
                pending.ResolvedUrl,
                result.RoundId,
                result.SessionId,
                result.RidersCreated,
                result.RidersUpdated,
                result.ResultsUpserted,
                result.SplitsUpserted,
                result.PointsRecalculated,
                itemWarnings));
        }

        var roundId = items.Select(item => item.RoundId).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        var imageUpserted = false;
        if (image is not null)
        {
            if (string.IsNullOrWhiteSpace(roundId))
            {
                throw new ApiException(
                    StatusCodes.Status500InternalServerError,
                    "ROUND_IMAGE_ROUND_ID_MISSING",
                    "Round was imported, but its identifier is unavailable for image storage.");
            }

            await UpsertRoundImageAsync(roundId, image, cancellationToken);
            imageUpserted = true;
        }

        return new ChronoRaceRoundFormImportResponse(
            roundId,
            items.Select(item => item.SessionId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            items.Count,
            items.Sum(item => item.RidersCreated),
            items.Sum(item => item.RidersUpdated),
            items.Sum(item => item.ResultsUpserted),
            items.Sum(item => item.SplitsUpserted),
            items.Count(item => item.PointsRecalculated),
            items,
            allWarnings,
            imageUpserted);
    }

    private async Task UpsertRoundImageAsync(
        string roundId,
        PreparedRoundImage image,
        CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["round_id"] = roundId,
            ["image_data"] = $"\\x{Convert.ToHexString(image.Bytes)}",
            ["content_type"] = "image/png",
            ["file_name"] = image.FileName,
            ["alt_text"] = image.AltText,
            ["width_px"] = image.WidthPx,
            ["height_px"] = image.HeightPx
        };

        await _supabase.PostAsync(
            "round_images",
            payload,
            SupabaseRole.Service,
            onConflict: "round_id",
            cancellationToken: cancellationToken);
    }

    private static void ValidateRequest(ChronoRaceRoundFormImportRequest request)
    {
        if (request.Round is null)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "ROUND_REQUIRED", "round is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Round.ContextName))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "ROUND_CONTEXT_NAME_REQUIRED", "round.context_name is required.");
        }

        if (request.Track is null)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "TRACK_REQUIRED", "track is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Track.Name))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "TRACK_NAME_REQUIRED", "track.name is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Track.Venue))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "TRACK_VENUE_REQUIRED", "track.venue is required.");
        }

        if (request.CompetitionClass is null)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "COMPETITION_CLASS_REQUIRED", "competition_class is required.");
        }

        if (request.Sessions is null || request.Sessions.Count == 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "SESSIONS_REQUIRED", "At least one session is required.");
        }

        foreach (var session in request.Sessions)
        {
            if (string.IsNullOrWhiteSpace(session.Name))
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "SESSION_NAME_REQUIRED", "session.name is required.");
            }

            if (string.IsNullOrWhiteSpace(session.SessionType))
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "SESSION_TYPE_REQUIRED", $"session_type is required for session '{session.Name}'.");
            }

            if (session.Condition is null && request.Condition is null)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "SESSION_CONDITION_REQUIRED", $"condition is required for session '{session.Name}'.");
            }

            if (session.Categories is null || session.Categories.Count == 0)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "SESSION_CATEGORIES_REQUIRED", $"At least one category is required for session '{session.Name}'.");
            }

            foreach (var category in session.Categories)
            {
                if (string.IsNullOrWhiteSpace(category.CategoryCode))
                {
                    throw new ApiException(StatusCodes.Status400BadRequest, "CATEGORY_CODE_REQUIRED", $"category_code is required for session '{session.Name}'.");
                }

                if (category.ChronoraceJson is not null && category.ResultJson is not null)
                {
                    throw new ApiException(
                        StatusCodes.Status400BadRequest,
                        "MULTIPLE_RESULT_SOURCES",
                        $"Provide either result_json or chronorace_json for session '{session.Name}' and category '{category.CategoryCode}', not both.");
                }

                if (category.ChronoraceJson is null &&
                    category.ResultJson is null &&
                    string.IsNullOrWhiteSpace(category.ChronoraceUrl) &&
                    string.IsNullOrWhiteSpace(category.ResultUrl))
                {
                    throw new ApiException(
                        StatusCodes.Status400BadRequest,
                        "RESULT_SOURCE_REQUIRED",
                        $"Either result_json, result_url, chronorace_json, or chronorace_url is required for session '{session.Name}' and category '{category.CategoryCode}'.");
                }
            }
        }
    }

    private sealed record PendingChronoRaceImport(
        ChronoRaceRoundFormSessionPayload Session,
        ChronoRaceRoundFormCategoryPayload Category,
        JsonObject Json,
        string? ResolvedUrl,
        IReadOnlyList<string> FetchWarnings);
}
