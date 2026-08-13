using System.Globalization;
using System.Text.Json.Nodes;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Models;

namespace UCI_Stats_API.Services;

public sealed class AdminSessionReimportService
{
    private readonly ISupabaseRestClient _supabase;
    private readonly ChronoRaceJsonFetcher _jsonFetcher;
    private readonly ChronoRaceImportService _sessionImport;

    public AdminSessionReimportService(
        ISupabaseRestClient supabase,
        ChronoRaceJsonFetcher jsonFetcher,
        ChronoRaceImportService sessionImport)
    {
        _supabase = supabase;
        _jsonFetcher = jsonFetcher;
        _sessionImport = sessionImport;
    }

    public async Task<ChronoRaceSessionReimportResponse> ReimportAsync(
        string sessionId,
        ChronoRaceSessionReimportRequest? request,
        CancellationToken cancellationToken)
    {
        EnsurePositiveId(sessionId);
        if (request is null ||
            (request.ChronoraceJson is null && request.ResultJson is null && string.IsNullOrWhiteSpace(request.ChronoraceUrl)))
        {
            throw Invalid("RESULT_SOURCE_REQUIRED", "Either result_json, chronorace_json, or chronorace_url is required.");
        }

        if (request.ChronoraceJson is not null && request.ResultJson is not null)
        {
            throw Invalid("MULTIPLE_RESULT_SOURCES", "Provide either result_json or chronorace_json, not both.");
        }

        JsonObject json;
        string? resolvedUrl = null;
        var fetchWarnings = new List<string>();
        var inlineJson = request.ResultJson ?? request.ChronoraceJson;
        if (inlineJson is not null)
        {
            if (!ImportResultJsonNormalizer.IsSupported(inlineJson))
            {
                throw Invalid("INVALID_RESULT_JSON", "result_json is not a supported ChronoRace or UCI World Championship result document.");
            }

            json = inlineJson;
        }
        else
        {
            var fetched = await _jsonFetcher.FetchAsync(request.ChronoraceUrl!, cancellationToken);
            json = fetched.Json;
            resolvedUrl = fetched.ResolvedUrl;
            fetchWarnings.AddRange(fetched.Warnings);
        }

        var session = await RequiredByIdAsync("sessions", sessionId, "SESSION_NOT_FOUND", cancellationToken);
        var round = await RequiredByIdAsync("rounds", RequiredId(session, "round_id"), "ROUND_NOT_FOUND", cancellationToken);
        var track = await RequiredByIdAsync("tracks", RequiredId(round, "track_id"), "TRACK_NOT_FOUND", cancellationToken);
        var competitionClass = await RequiredByIdAsync(
            "competition_levels",
            RequiredId(round, "competition_level_id"),
            "COMPETITION_CLASS_NOT_FOUND",
            cancellationToken);
        var conditionId = session.String("condition_id") ?? RequiredId(round, "condition_id");
        var condition = await RequiredByIdAsync("conditions", conditionId, "CONDITION_NOT_FOUND", cancellationToken);
        var category = await RequiredByIdAsync("categories", RequiredId(session, "category_id"), "CATEGORY_NOT_FOUND", cancellationToken);

        var importRequest = new ChronoRaceImportRequest(
            new ImportRoundPayload(
                RequiredText(round, "context_name"),
                round.Int("season_year") ?? throw Invalid("ROUND_SEASON_REQUIRED", "The existing round has no season_year."),
                round.Int("round_number"),
                ParseDateTime(round.String("start_date")),
                ParseDateTime(round.String("end_date")),
                CompetitionClassId: long.Parse(RequiredId(round, "competition_level_id"), CultureInfo.InvariantCulture),
                IsSeasonFinalRound: round.Bool("is_season_final_round") == true),
            new ImportTrackPayload(
                RequiredText(track, "name"),
                RequiredText(track, "venue"),
                track.String("country_code"),
                track.Int("length_m"),
                track.Decimal("avg_slope")),
            new ImportCompetitionClassPayload(
                RequiredText(competitionClass, "code"),
                RequiredText(competitionClass, "name"),
                competitionClass.Int("priority")),
            new ImportConditionPayload(
                condition.Int("id"),
                RequiredText(condition, "name"),
                condition.Bool("is_dry")),
            RequiredText(category, "code"),
            new ImportSessionPayload(
                RequiredText(session, "name"),
                RequiredText(session, "session_type"),
                ParseDate(session.String("session_date")),
                RequiredText(session, "status")),
            json);

        var imported = await _sessionImport.ImportSessionAsync(importRequest, cancellationToken);
        if (!string.Equals(imported.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiException(
                StatusCodes.Status409Conflict,
                "REIMPORT_SESSION_MISMATCH",
                $"Reimport resolved to session '{imported.SessionId}' instead of requested session '{sessionId}'.");
        }

        var warnings = fetchWarnings.Concat(imported.Warnings).ToArray();
        return new ChronoRaceSessionReimportResponse(
            sessionId,
            request.ChronoraceUrl,
            resolvedUrl,
            imported,
            warnings);
    }

    private async Task<JsonObject> RequiredByIdAsync(
        string table,
        string id,
        string code,
        CancellationToken cancellationToken)
    {
        return await _supabase.GetSingleAsync(
                new SupabaseQuery(table).Select("*").Filter("id", "eq", id),
                SupabaseRole.Service,
                cancellationToken)
            ?? throw new ApiException(StatusCodes.Status404NotFound, code, $"{table} resource '{id}' was not found.");
    }

    private static string RequiredId(JsonObject row, string field)
    {
        var value = row.String(field);
        if (string.IsNullOrWhiteSpace(value) || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            throw Invalid("INVALID_REIMPORT_REFERENCE", $"Existing {field} is missing or invalid.");
        }

        return value;
    }

    private static string RequiredText(JsonObject row, string field)
    {
        var value = row.String(field)?.Trim();
        return string.IsNullOrWhiteSpace(value)
            ? throw Invalid("INVALID_REIMPORT_DATA", $"Existing {field} is missing or blank.")
            : value;
    }

    private static DateOnly? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw Invalid("INVALID_REIMPORT_DATA", "Existing session_date is invalid.");
    }

    private static DateTimeOffset? ParseDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dateTime)
            ? dateTime
            : throw Invalid("INVALID_REIMPORT_DATA", "Existing round date is invalid.");
    }

    private static void EnsurePositiveId(string id)
    {
        if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            throw Invalid("INVALID_ADMIN_ID", "session_id must be a positive integer.");
        }
    }

    private static ApiException Invalid(string code, string message)
    {
        return new ApiException(StatusCodes.Status400BadRequest, code, message);
    }
}
