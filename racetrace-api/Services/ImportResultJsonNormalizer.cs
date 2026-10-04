using System.Globalization;
using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure;

namespace RaceTrace.Api.Services;

public static class ImportResultJsonNormalizer
{
    public const string ChronoRaceFormat = "chronorace";
    public const string UciWorldChampionshipFormat = "uci_world_championship";

    public static bool IsSupported(JsonObject json)
    {
        return ChronoRaceJsonFetcher.IsChronoRaceResultJson(json) ||
            UciWorldChampionshipJsonAdapter.IsResultJson(json);
    }

    public static NormalizedImportResultJson Normalize(
        JsonObject json,
        string competitionClassCode,
        string categoryCode)
    {
        if (ChronoRaceJsonFetcher.IsChronoRaceResultJson(json))
        {
            return new NormalizedImportResultJson(json, ChronoRaceFormat);
        }

        if (!UciWorldChampionshipJsonAdapter.IsResultJson(json))
        {
            throw Invalid(
                "INVALID_RESULT_JSON",
                "Result JSON must contain either ChronoRace Riders/Results or UCI World Championship event/results data.");
        }

        if (!string.Equals(competitionClassCode, "world_championship", StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid(
                "RESULT_FORMAT_COMPETITION_CLASS_MISMATCH",
                "UCI World Championship result JSON requires competition_class_code 'world_championship'.");
        }

        var detectedCategory = UciWorldChampionshipJsonAdapter.DetectCategoryCode(json);
        if (detectedCategory is not null &&
            !string.Equals(detectedCategory, NormalizeCategory(categoryCode), StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid(
                "RESULT_FORMAT_CATEGORY_MISMATCH",
                $"The UCI result JSON describes category '{detectedCategory}', but the import selected '{categoryCode}'.");
        }

        return new NormalizedImportResultJson(
            UciWorldChampionshipJsonAdapter.Normalize(json),
            UciWorldChampionshipFormat);
    }

    private static string NormalizeCategory(string categoryCode)
    {
        try
        {
            return PublicApiCodes.ToDatabaseCategory(categoryCode);
        }
        catch (ArgumentException)
        {
            return categoryCode.Trim().ToUpperInvariant();
        }
    }

    private static ApiException Invalid(string code, string message) =>
        new(StatusCodes.Status400BadRequest, code, message);
}

public static class UciWorldChampionshipJsonAdapter
{
    public static bool IsResultJson(JsonObject json)
    {
        return json.Object("event") is not null && json.Array("results") is not null;
    }

    public static JsonObject Normalize(JsonObject source)
    {
        if (!IsResultJson(source))
        {
            throw Invalid("INVALID_UCI_WORLD_CHAMPIONSHIP_JSON", "UCI result JSON must contain event and results.");
        }

        var sourceResults = source.Array("results")!.OfType<JsonObject>().ToArray();
        var leaderTimeMs = FindLeaderTimeMilliseconds(sourceResults);
        var riders = new JsonObject();
        var results = new JsonArray();

        for (var index = 0; index < sourceResults.Length; index++)
        {
            var sourceResult = sourceResults[index];
            var sourceRider = sourceResult.Object("rider");
            if (sourceRider is null)
            {
                throw Invalid(
                    "UCI_RESULT_RIDER_REQUIRED",
                    $"UCI result at index {index} has no rider object.");
            }

            var uciId = sourceRider.String("uciRiderId")?.Trim();
            if (string.IsNullOrWhiteSpace(uciId))
            {
                throw Invalid(
                    "UCI_RIDER_ID_REQUIRED",
                    $"UCI result at index {index} has no rider.uciRiderId.");
            }

            var technicalKey = uciId;
            var (familyName, givenName) = SplitRiderName(sourceRider.String("name"));
            var bib = sourceRider.Int("bib") ?? ParseInteger(sourceRider.String("key"));
            riders[technicalKey] = new JsonObject
            {
                ["RaceNr"] = bib,
                ["UciRiderId"] = uciId,
                ["FamilyName"] = familyName,
                ["GivenName"] = givenName,
                ["Nation"] = sourceRider.String("nation")?.Trim().ToUpperInvariant()
            };

            var status = ResultStatus(sourceResult);
            var position = sourceResult.Int("rank") is > 0 ? sourceResult.Int("rank") : null;
            int? raceTimeMs = string.Equals(status, "Finished", StringComparison.OrdinalIgnoreCase)
                ? ResolveFinishTimeMilliseconds(sourceResult, leaderTimeMs)
                : null;
            var times = NormalizeSplits(sourceResult.Array("splits"));
            results.Add(new JsonObject
            {
                ["RaceNr"] = technicalKey,
                ["Position"] = position,
                ["RaceTime"] = raceTimeMs,
                ["CompletedDistance"] = null,
                ["Status"] = status,
                ["InResult"] = position is not null && raceTimeMs is > 0 && status == "Finished",
                ["Speed"] = ParseDecimal(sourceResult.String("speed")),
                ["Times"] = times
            });
        }

        return new JsonObject
        {
            ["Riders"] = riders,
            ["Results"] = results
        };
    }

    public static string? DetectCategoryCode(JsonObject source)
    {
        var name = source.Object("event")?.String("name")?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (name.Contains("women junior", StringComparison.Ordinal) ||
            name.Contains("junior women", StringComparison.Ordinal))
        {
            return "JW";
        }

        if (name.Contains("men junior", StringComparison.Ordinal) ||
            name.Contains("junior men", StringComparison.Ordinal))
        {
            return "JM";
        }

        if (name.Contains("women elite", StringComparison.Ordinal) ||
            name.Contains("elite women", StringComparison.Ordinal))
        {
            return "EW";
        }

        if (name.Contains("men elite", StringComparison.Ordinal) ||
            name.Contains("elite men", StringComparison.Ordinal))
        {
            return "EM";
        }

        return null;
    }

    internal static int? ParseTimeMilliseconds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().TrimStart('+').Replace(',', '.');
        var parts = normalized.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 3 ||
            !decimal.TryParse(parts[^1], NumberStyles.Number, CultureInfo.InvariantCulture, out var seconds) ||
            seconds < 0 || seconds >= 60 && parts.Length > 1)
        {
            return null;
        }

        decimal totalSeconds = seconds;
        if (parts.Length >= 2)
        {
            if (!int.TryParse(parts[^2], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
                minutes < 0 || minutes >= 60 && parts.Length == 3)
            {
                return null;
            }

            totalSeconds += minutes * 60m;
        }

        if (parts.Length == 3)
        {
            if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) || hours < 0)
            {
                return null;
            }

            totalSeconds += hours * 3600m;
        }

        var milliseconds = decimal.Round(totalSeconds * 1000m, 0, MidpointRounding.AwayFromZero);
        return milliseconds <= int.MaxValue ? decimal.ToInt32(milliseconds) : null;
    }

    private static int? FindLeaderTimeMilliseconds(IReadOnlyList<JsonObject> results)
    {
        return results
            .Where(result => result.Int("rank") == 1 &&
                string.Equals(result.String("resultType"), "Time", StringComparison.OrdinalIgnoreCase))
            .Select(ResultTimeText)
            .Where(value => !string.IsNullOrWhiteSpace(value) && !value!.TrimStart().StartsWith('+'))
            .Select(ParseTimeMilliseconds)
            .FirstOrDefault(value => value is > 0);
    }

    private static int ResolveFinishTimeMilliseconds(JsonObject result, int? leaderTimeMs)
    {
        var value = ResultTimeText(result);
        var parsed = ParseTimeMilliseconds(value);
        if (parsed is null)
        {
            throw Invalid(
                "UCI_RESULT_TIME_INVALID",
                $"Could not parse finish time '{value}' for rider '{result.Object("rider")?.String("uciRiderId")}'.");
        }

        if (!value!.TrimStart().StartsWith('+'))
        {
            return parsed.Value;
        }

        if (leaderTimeMs is null)
        {
            throw Invalid(
                "UCI_LEADER_TIME_REQUIRED",
                "The UCI result JSON contains time gaps, but no absolute leader time was found.");
        }

        try
        {
            return checked(leaderTimeMs.Value + parsed.Value);
        }
        catch (OverflowException)
        {
            throw Invalid("UCI_RESULT_TIME_INVALID", "Calculated UCI finish time exceeds the supported range.");
        }
    }

    private static JsonArray NormalizeSplits(JsonArray? sourceSplits)
    {
        var times = new JsonArray();
        if (sourceSplits is null)
        {
            return times;
        }

        var sourceRows = sourceSplits.OfType<JsonObject>().ToArray();
        for (var index = 0; index < sourceRows.Length; index++)
        {
            var split = sourceRows[index];
            if (!string.Equals(split.String("resultType"), "Time", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var raceTimeMs = ParseTimeMilliseconds(split.String("value"));
            if (raceTimeMs is not > 0)
            {
                continue;
            }

            times.Add(new JsonObject
            {
                ["SplitIndex"] = index + 1,
                ["RaceTime"] = raceTimeMs,
                ["Speed"] = null
            });
        }

        return times;
    }

    private static string ResultStatus(JsonObject result)
    {
        if (string.Equals(result.String("resultType"), "Time", StringComparison.OrdinalIgnoreCase))
        {
            return "Finished";
        }

        var status = result.String("value")?.Trim().ToUpperInvariant();
        return status is "DNF" or "DNS" or "DSQ" ? status : "IRM";
    }

    private static string? ResultTimeText(JsonObject result)
    {
        return FirstNonBlank(result.String("time"), result.String("value"), result.String("gap"));
    }

    private static (string? FamilyName, string? GivenName) SplitRiderName(string? value)
    {
        var parts = value?
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        if (parts.Length == 0)
        {
            return (null, null);
        }

        var familyPartCount = 0;
        while (familyPartCount < parts.Length && IsUpperNamePart(parts[familyPartCount]))
        {
            familyPartCount++;
        }

        if (familyPartCount == 0)
        {
            familyPartCount = 1;
        }

        return (
            string.Join(' ', parts.Take(familyPartCount)),
            familyPartCount < parts.Length ? string.Join(' ', parts.Skip(familyPartCount)) : null);
    }

    private static bool IsUpperNamePart(string value)
    {
        var letters = value.Where(char.IsLetter).ToArray();
        return letters.Length > 0 && letters.All(char.IsUpper);
    }

    private static int? ParseInteger(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static decimal? ParseDecimal(string? value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static ApiException Invalid(string code, string message) =>
        new(StatusCodes.Status400BadRequest, code, message);
}

public sealed record NormalizedImportResultJson(JsonObject Json, string Format);
