using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Models;

namespace UCI_Stats_API.Services;

public sealed class UciMtbWorldSeriesSupplementService
{
    private static readonly Regex DurationPattern = new(
        @"^(?:(?<hours>\d+):)?(?<minutes>\d{1,2}):(?<seconds>\d{2})\.(?<milliseconds>\d{3})$",
        RegexOptions.CultureInvariant);

    private static readonly Regex SplitPattern = new(
        @"^Split\s+(?<index>\d+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly ISupabaseRestClient _supabase;
    private readonly IPointAwardService _pointAwards;

    public UciMtbWorldSeriesSupplementService(
        ISupabaseRestClient supabase,
        IPointAwardService pointAwards)
    {
        _supabase = supabase;
        _pointAwards = pointAwards;
    }

    public async Task<UciMtbWorldSeriesSupplementResponse> SupplementAsync(
        string sessionId,
        JsonObject? payload,
        CancellationToken cancellationToken)
    {
        EnsurePositiveId(sessionId);
        if (payload is null)
        {
            throw Invalid("UCI_RESULTS_JSON_REQUIRED", "The UCI MTB World Series JSON body is required.");
        }

        if (payload.Bool("error") == true)
        {
            throw Invalid("UCI_RESULTS_SOURCE_ERROR", "The supplied UCI MTB World Series response reports an error.");
        }

        var sourceArray = payload.Array("results");
        if (sourceArray is null || sourceArray.Count == 0)
        {
            throw Invalid("UCI_RESULTS_REQUIRED", "The JSON body must contain a non-empty results array.");
        }

        var session = await _supabase.GetSingleAsync(
            new SupabaseQuery("sessions").Select("id").Filter("id", "eq", sessionId),
            SupabaseRole.Service,
            cancellationToken);
        if (session is null)
        {
            throw new ApiException(StatusCodes.Status404NotFound, "SESSION_NOT_FOUND", $"Session '{sessionId}' was not found.");
        }

        var existingRows = await _supabase.GetAsync(
            new SupabaseQuery("results")
                .Select("id,rider_id,race_time_ms,position,is_classified,status,riders(id,uci_id,first_name,last_name)")
                .Filter("session_id", "eq", sessionId)
                .Limit(500),
            role: SupabaseRole.Service,
            cancellationToken: cancellationToken);

        if (existingRows.Data.Count == 0)
        {
            throw new ApiException(
                StatusCodes.Status409Conflict,
                "SESSION_RESULTS_REQUIRED",
                "The session has no existing results to supplement. Import the ChronoRace session first.");
        }

        var warnings = new List<string>();
        var sourceResults = ParseSourceResults(sourceArray, warnings);
        if (sourceResults.Count == 0)
        {
            throw Invalid("NO_VALID_UCI_RESULTS", "The JSON body does not contain any valid results with cumulative times.");
        }

        var existingResults = existingRows.Data
            .Select(ParseExistingResult)
            .Where(result => result is not null)
            .Cast<ExistingResult>()
            .ToArray();

        var matches = MatchResults(sourceResults, existingResults, warnings);
        var updated = 0;
        var splitsUpserted = 0;

        foreach (var match in matches)
        {
            var source = match.Source;
            var values = new JsonObject
            {
                ["position"] = source.IsClassified ? source.Position : null,
                ["race_time_ms"] = source.FinishTimeMs,
                ["status"] = source.Status,
                ["is_classified"] = source.IsClassified
            };

            var saved = await _supabase.PatchAsync(
                "results",
                values,
                new SupabaseQuery("results").Filter("id", "eq", match.Existing.Id),
                SupabaseRole.Service,
                cancellationToken);
            if (saved.Count == 0)
            {
                throw new ApiException(
                    StatusCodes.Status409Conflict,
                    "SUPPLEMENT_RESULT_NOT_UPDATED",
                    $"Result '{match.Existing.Id}' disappeared before it could be updated.");
            }

            var splitRows = new JsonArray(source.Splits
                .Select(split => (JsonNode)new JsonObject
                {
                    ["result_id"] = match.Existing.Id,
                    ["split_index"] = split.Index,
                    ["race_time"] = split.RaceTimeMs
                })
                .ToArray());

            await _supabase.DeleteAsync(
                "time_splits",
                new SupabaseQuery("time_splits").Filter("result_id", "eq", match.Existing.Id),
                SupabaseRole.Service,
                cancellationToken);
            await _supabase.PostAsync(
                "time_splits",
                splitRows,
                SupabaseRole.Service,
                cancellationToken: cancellationToken);

            updated++;
            splitsUpserted += source.Splits.Count;
        }

        var pointsRecalculated = updated > 0 &&
            await TryRecalculatePointsAsync(sessionId, warnings, cancellationToken);
        if (updated > 0)
        {
            await TryRefreshRankingPreaggregateAsync(sessionId, warnings, cancellationToken);
        }

        return new UciMtbWorldSeriesSupplementResponse(
            sessionId,
            sourceArray.Count,
            matches.Count,
            updated,
            splitsUpserted,
            sourceArray.Count - matches.Count,
            pointsRecalculated,
            warnings);
    }

    internal static bool TryParseDurationMilliseconds(string? value, out int milliseconds)
    {
        milliseconds = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var match = DurationPattern.Match(value.Trim());
        var hours = 0;
        if (!match.Success ||
            (match.Groups["hours"].Success &&
                !int.TryParse(match.Groups["hours"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out hours)) ||
            !int.TryParse(match.Groups["minutes"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            !int.TryParse(match.Groups["seconds"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            !int.TryParse(match.Groups["milliseconds"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var fraction) ||
            seconds >= 60 ||
            (hours > 0 && minutes >= 60))
        {
            return false;
        }

        var total = ((long)hours * 60 * 60 * 1000) + ((long)minutes * 60 * 1000) + (seconds * 1000L) + fraction;
        if (total <= 0 || total > int.MaxValue)
        {
            return false;
        }

        milliseconds = (int)total;
        return true;
    }

    private async Task TryRefreshRankingPreaggregateAsync(
        string sessionId,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            await _supabase.RpcAsync(
                "refresh_ranking_session_preaggregate",
                new JsonObject { ["p_session_id"] = long.Parse(sessionId, CultureInfo.InvariantCulture) },
                SupabaseRole.Service,
                cancellationToken);
        }
        catch (SupabaseApiException)
        {
            warnings.Add(
                "Could not refresh ranking preaggregation for this session. " +
                "Supplement finished successfully and ranking will use the compatibility path.");
        }
    }

    private static IReadOnlyList<SourceResult> ParseSourceResults(
        JsonArray sourceArray,
        List<string> warnings)
    {
        var results = new List<SourceResult>();
        for (var index = 0; index < sourceArray.Count; index++)
        {
            if (sourceArray[index] is not JsonObject source)
            {
                warnings.Add($"Source result at index {index} was skipped because it is not an object.");
                continue;
            }

            var sourceKey = FirstNonBlank(source.String("resultId"), source.String("riderName")) ?? $"index {index}";
            var rider = source.Object("rider");
            var riderName = FirstNonBlank(rider?.String("fullName"), source.String("riderName"));
            if (string.IsNullOrWhiteSpace(riderName))
            {
                warnings.Add($"Source result '{sourceKey}' was skipped because riderName is missing.");
                continue;
            }

            if (!TryParseDurationMilliseconds(source.String("resultTime"), out var finishTimeMs))
            {
                warnings.Add($"Source result '{sourceKey}' was skipped because resultTime is missing or invalid.");
                continue;
            }

            var splits = ParseSplits(source, sourceKey, finishTimeMs, warnings);
            if (splits is null || splits.Count == 0)
            {
                continue;
            }

            var invalidCode = source.String("resultInvalidCode")?.Trim();
            var position = source.Int("resultPosition");
            var isClassified = string.IsNullOrWhiteSpace(invalidCode) && position is > 0;
            results.Add(new SourceResult(
                sourceKey,
                riderName.Trim(),
                NormalizeName(riderName),
                NormalizeNameParts(riderName),
                NormalizeUciId(FirstNonBlank(rider?.String("uciid"), rider?.String("uciId"), source.String("riderUciId"))),
                finishTimeMs,
                position is > 0 ? position : null,
                isClassified,
                isClassified ? "Finished" : invalidCode?.ToUpperInvariant() ?? "Unclassified",
                splits));
        }

        return results;
    }

    private static IReadOnlyList<SourceSplit>? ParseSplits(
        JsonObject source,
        string sourceKey,
        int finishTimeMs,
        List<string> warnings)
    {
        var stages = source.Array("apiRaceStageResults") ?? new JsonArray();
        var splits = new Dictionary<int, SourceSplit>();

        foreach (var stage in stages.OfType<JsonObject>())
        {
            var stageName = stage.String("SplitStage")?.Trim();
            if (string.Equals(stageName, "Finish", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseDurationMilliseconds(stage.String("Time"), out var finishStageTimeMs) ||
                    finishStageTimeMs != finishTimeMs)
                {
                    warnings.Add($"Source result '{sourceKey}' was skipped because Finish time differs from resultTime.");
                    return null;
                }

                continue;
            }

            var splitIndex = ParseSplitIndex(stageName);
            if (splitIndex is null or <= 0)
            {
                warnings.Add($"Source result '{sourceKey}' contains unsupported stage '{stageName ?? "<missing>"}', which was ignored.");
                continue;
            }

            if (!TryParseDurationMilliseconds(stage.String("Time"), out var raceTimeMs))
            {
                warnings.Add($"Source result '{sourceKey}' contains an invalid time for stage '{stageName}', which was ignored.");
                continue;
            }

            if (!splits.TryAdd(splitIndex.Value, new SourceSplit(splitIndex.Value, raceTimeMs)))
            {
                warnings.Add($"Source result '{sourceKey}' was skipped because stage '{stageName}' is duplicated.");
                return null;
            }
        }

        var ordered = splits.Values.OrderBy(split => split.Index).ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index].RaceTimeMs <= ordered[index - 1].RaceTimeMs)
            {
                warnings.Add($"Source result '{sourceKey}' was skipped because cumulative stage times are not increasing.");
                return null;
            }
        }

        return ordered;
    }

    private static ExistingResult? ParseExistingResult(JsonObject row)
    {
        var id = row.String("id");
        var rider = row.Object("riders");
        if (string.IsNullOrWhiteSpace(id) || rider is null)
        {
            return null;
        }

        var name = string.Join(" ", new[] { rider.String("first_name"), rider.String("last_name") }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        return new ExistingResult(
            id,
            NormalizeName(name),
            NormalizeNameParts(name),
            NormalizeUciId(rider.String("uci_id")),
            row.Int("race_time_ms"));
    }

    private static IReadOnlyList<ResultMatch> MatchResults(
        IReadOnlyList<SourceResult> sourceResults,
        IReadOnlyList<ExistingResult> existingResults,
        List<string> warnings)
    {
        var matches = new List<ResultMatch>();
        var usedExistingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sourceResults)
        {
            ExistingResult? existing = null;
            if (!string.IsNullOrWhiteSpace(source.UciId))
            {
                var uciMatches = existingResults
                    .Where(candidate => string.Equals(candidate.UciId, source.UciId, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (uciMatches.Length != 1)
                {
                    warnings.Add($"Source result '{source.SourceKey}' was skipped because UCI ID '{source.UciId}' matched {uciMatches.Length} session results.");
                    continue;
                }

                existing = uciMatches[0];
                if (!string.Equals(existing.NormalizedName, source.NormalizedName, StringComparison.Ordinal))
                {
                    warnings.Add($"Source result '{source.SourceKey}' was matched by UCI ID although its rider name differs from the database.");
                }
            }
            else
            {
                var nameMatches = existingResults
                    .Where(candidate => string.Equals(candidate.NormalizedName, source.NormalizedName, StringComparison.Ordinal))
                    .ToArray();
                if (nameMatches.Length == 1)
                {
                    existing = nameMatches[0];
                    if (existing.RaceTimeMs is > 0 && existing.RaceTimeMs != source.FinishTimeMs)
                    {
                        warnings.Add($"Source result '{source.SourceKey}' was skipped because its finish time does not match the existing result for that rider.");
                        continue;
                    }
                }
                else if (nameMatches.Length == 0)
                {
                    var preferredNameMatches = existingResults
                        .Where(candidate => candidate.RaceTimeMs == source.FinishTimeMs &&
                            AreNamePartsCompatible(source.NameParts, candidate.NameParts))
                        .ToArray();
                    if (preferredNameMatches.Length != 1)
                    {
                        warnings.Add($"Source result '{source.SourceKey}' was skipped because its full rider name and finish time matched {preferredNameMatches.Length} session results.");
                        continue;
                    }

                    existing = preferredNameMatches[0];
                    warnings.Add($"Source result '{source.SourceKey}' was matched by unique finish time and a compatible preferred rider name.");
                }
                else
                {
                    warnings.Add($"Source result '{source.SourceKey}' was skipped because the exact full rider name matched {nameMatches.Length} session results.");
                    continue;
                }
            }

            if (!usedExistingIds.Add(existing.Id))
            {
                warnings.Add($"Source result '{source.SourceKey}' was skipped because another source row already matched result '{existing.Id}'.");
                continue;
            }

            matches.Add(new ResultMatch(source, existing));
        }

        return matches;
    }

    private async Task<bool> TryRecalculatePointsAsync(
        string sessionId,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _pointAwards.RecalculateSessionAsync(sessionId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(result.Warning))
            {
                warnings.Add(result.Warning);
            }

            return result.PointSystemApplied;
        }
        catch (SupabaseApiException)
        {
            warnings.Add("Could not recalculate session points. Results were supplemented without recalculating points.");
            return false;
        }
    }

    private static int? ParseSplitIndex(string? stageName)
    {
        if (string.IsNullOrWhiteSpace(stageName))
        {
            return null;
        }

        var match = SplitPattern.Match(stageName.Trim());
        return match.Success &&
            int.TryParse(match.Groups["index"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                ? index
                : null;
    }

    private static string NormalizeName(string value)
    {
        var builder = new StringBuilder();
        foreach (var character in value.Trim().Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark || !char.IsLetterOrDigit(character))
            {
                continue;
            }

            builder.Append(char.ToUpperInvariant(character));
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static IReadOnlyList<string> NormalizeNameParts(string value)
    {
        return value
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeName)
            .Where(part => part.Length > 0)
            .ToArray();
    }

    private static bool AreNamePartsCompatible(
        IReadOnlyList<string> sourceParts,
        IReadOnlyList<string> existingParts)
    {
        if (sourceParts.Count < 2 || existingParts.Count < sourceParts.Count)
        {
            return false;
        }

        for (var offset = 0; offset <= existingParts.Count - sourceParts.Count; offset++)
        {
            var matches = true;
            for (var index = 0; index < sourceParts.Count; index++)
            {
                if (!string.Equals(sourceParts[index], existingParts[offset + index], StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return true;
            }
        }

        return false;
    }

    private static string? NormalizeUciId(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
    }

    private static string? FirstNonBlank(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
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

    private sealed record SourceResult(
        string SourceKey,
        string RiderName,
        string NormalizedName,
        IReadOnlyList<string> NameParts,
        string? UciId,
        int FinishTimeMs,
        int? Position,
        bool IsClassified,
        string Status,
        IReadOnlyList<SourceSplit> Splits);

    private sealed record SourceSplit(int Index, int RaceTimeMs);

    private sealed record ExistingResult(
        string Id,
        string NormalizedName,
        IReadOnlyList<string> NameParts,
        string? UciId,
        int? RaceTimeMs);

    private sealed record ResultMatch(SourceResult Source, ExistingResult Existing);
}
