using System.Text.Json.Nodes;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Features.Imports.ChronoRace;

/// <summary>Pure normalization and payload preparation. Persistence, batching and retries remain in the importer.</summary>
internal static class ChronoRacePayloadMapper
{
    internal static JsonObject BuildSessionPayload(
        ImportSessionPayload payload,
        string roundId,
        string categoryId,
        string? conditionId,
        string sessionType)
    {
        var values = new JsonObject
        {
            ["round_id"] = roundId,
            ["category_id"] = categoryId,
            ["name"] = payload.Name,
            ["session_type"] = sessionType,
            ["condition_id"] = conditionId,
            ["session_date"] = payload.SessionDate?.ToString("yyyy-MM-dd"),
            ["status"] = string.IsNullOrWhiteSpace(payload.Status) ? "scheduled" : payload.Status
        };

        // The public ChronoRace JSON URL lets the frontend poll live timing
        // directly. Inline/PDF imports deliberately keep an existing source.
        if (!string.IsNullOrWhiteSpace(payload.ChronoraceUrl))
        {
            values["chronorace_url"] = payload.ChronoraceUrl.Trim();
        }

        return values;
    }

    internal static JsonObject BuildResultPayload(
        string sessionId,
        string riderId,
        string? teamId,
        JsonObject result,
        JsonObject chronoRider,
        int? position,
        bool isClassified)
    {
        return new JsonObject
        {
            ["session_id"] = sessionId,
            ["rider_id"] = riderId,
            ["team_id"] = teamId,
            ["race_nr"] = chronoRider.Int("RaceNr"),
            ["position"] = position,
            ["race_time_ms"] = result.Int("RaceTime"),
            ["completed_distance"] = result.Int("CompletedDistance"),
            ["status"] = result.String("Status"),
            ["top_speed"] = result.Decimal("Speed"),
            ["is_classified"] = isClassified
        };
    }

    internal static JsonObject[] TimingSplitsWithoutFinish(
        JsonArray times,
        int? finishTimeMs)
    {
        var timingRows = times.OfType<JsonObject>().ToArray();
        if (finishTimeMs is not > 0)
        {
            return timingRows;
        }

        return timingRows
            .Where(time => time.Int("RaceTime") != finishTimeMs)
            .ToArray();
    }

    internal static string? GenderFromCategory(string? categoryCode)
    {
        if (string.IsNullOrWhiteSpace(categoryCode))
        {
            return null;
        }

        string normalized;
        try
        {
            normalized = PublicApiCodes.ToDatabaseCategory(categoryCode);
        }
        catch (ArgumentException)
        {
            normalized = categoryCode.Trim().ToUpperInvariant();
        }

        return normalized switch
        {
            "EM" or "JM" or "ME" or "MJ" => "male",
            "EW" or "JW" or "WE" or "WJ" => "female",
            _ => null
        };
    }

    internal static JsonObject BuildRiderPayload(
        JsonObject riderNode,
        JsonObject? existing,
        string? gender)
    {
        return new JsonObject
        {
            ["uci_id"] = PreferNew(riderNode.String("UciRiderId"), existing?.String("uci_id")),
            ["uci_code"] = PreferNew(riderNode.String("UciCode"), existing?.String("uci_code")),
            ["last_name"] = PreferNew(riderNode.String("FamilyName"), existing?.String("last_name")),
            ["first_name"] = PreferNew(riderNode.String("GivenName"), existing?.String("first_name")),
            ["nation"] = PreferNew(riderNode.String("Nation"), existing?.String("nation")),
            ["birth_date"] = ResolveBirthDate(riderNode, existing),
            ["gender"] = PreferNew(gender, existing?.String("gender")),
            ["is_protected"] = riderNode.Bool("Protected") ?? existing?.Bool("is_protected") ?? false,
            ["has_injury"] = riderNode.Bool("Injury") ?? existing?.Bool("has_injury") ?? false
        };
    }

    internal static int? NormalizePosition(int? position)
    {
        return position is > 0 ? position : null;
    }

    internal static string? PreferNew(string? newValue, string? oldValue)
    {
        return string.IsNullOrWhiteSpace(newValue) ? oldValue : newValue;
    }

    internal static string? FirstNonBlank(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    internal static string? NormalizeDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, out var dateTime)
            ? dateTime.ToString("yyyy-MM-dd")
            : value;
    }

    internal static string? ResolveBirthDate(JsonObject riderNode, JsonObject? existing)
    {
        var exactSourceDate = NormalizeDate(riderNode.String("BirthDate"));
        if (!string.IsNullOrWhiteSpace(exactSourceDate))
        {
            return exactSourceDate;
        }

        var existingDate = existing?.String("birth_date");
        if (!string.IsNullOrWhiteSpace(existingDate))
        {
            return existingDate;
        }

        var year = riderNode.Int("YearOfBirth");
        return year is >= 1900 and <= 2100 ? $"{year:0000}-01-01" : null;
    }

    internal static bool IsClassified(JsonObject result)
    {
        var inResult = result.Bool("InResult") ?? false;
        var position = NormalizePosition(result.Int("Position"));
        var status = result.String("Status");
        return inResult && position is not null && result.Decimal("RaceTime") is > 0 && !string.Equals(status, "DNF", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "DNS", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "DSQ", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsFinishedResultWithoutSplits(JsonObject result, JsonArray times)
    {
        return times.OfType<JsonObject>().Any() is false &&
            result.Decimal("RaceTime") is > 0 &&
            string.Equals(result.String("Status"), "Finished", StringComparison.OrdinalIgnoreCase);
    }

}
