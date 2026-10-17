using System.Globalization;
using System.Text.Json.Nodes;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Infrastructure.Supabase;

/// <summary>Decodes the compact RPC wire format. Positional columns and legacy fallbacks are contract-sensitive.</summary>
internal static class CompactMetricsPackageParser
{
    internal static RankingDataBundle ParseRankingDataBundle(JsonNode? response)
    {
        var root = response as JsonObject ??
            (response as JsonArray)?.OfType<JsonObject>().FirstOrDefault() ??
            throw new ApiException(
                StatusCodes.Status502BadGateway,
                "RANKING_DATA_INVALID",
                "Supabase returned an invalid ranking data package.");

        return ParseRankingDataBundle([root]);
    }

    private static RankingDataBundle ParseRankingDataBundle(IReadOnlyList<JsonObject> packages)
        => ParseRankingReadData(packages).ToLegacy();

    internal static RankingReadData ParseRankingReadData(IReadOnlyList<JsonObject> packages)
    {
        // Imports can assign newer IDs to older seasons. Resolve identity snapshots
        // by race date, not insertion order, when combining session packages.
        packages = packages.OrderBy(package => CompactRows([package], "s")
            .Select(row => ParseTimestamp(CompactString(row, 5) ?? CompactString(row, 8)))
            .Max()).ToArray();
        var sessions = new List<JsonObject>();
        var sessionsById = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in CompactRows(packages, "s"))
        {
            var id = CompactString(row, 0);
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var competitionClass = new JsonObject
            {
                ["id"] = CompactClone(row, 12),
                ["code"] = CompactClone(row, 13),
                ["name"] = CompactClone(row, 14),
                ["priority"] = CompactClone(row, 9)
            };
            var condition = new JsonObject
            {
                ["id"] = CompactClone(row, 15),
                ["name"] = CompactClone(row, 16)
            };
            var track = new JsonObject
            {
                ["id"] = CompactClone(row, 6),
                ["name"] = CompactClone(row, 17),
                ["venue"] = CompactClone(row, 18),
                ["country_code"] = CompactClone(row, 19),
                ["length_m"] = CompactClone(row, 20),
                ["avg_slope"] = CompactClone(row, 21)
            };
            var round = new JsonObject
            {
                ["id"] = CompactClone(row, 1),
                ["round_number"] = CompactClone(row, 11),
                ["track_id"] = CompactClone(row, 6),
                ["season_year"] = CompactClone(row, 7),
                ["start_date"] = CompactClone(row, 8),
                ["competition_level_id"] = CompactClone(row, 12),
                ["competition_class_priority"] = CompactClone(row, 9),
                ["competition_levels"] = competitionClass,
                ["conditions"] = condition,
                ["tracks"] = track
            };
            var session = new JsonObject
            {
                ["id"] = CompactClone(row, 0),
                ["round_id"] = CompactClone(row, 1),
                ["category_id"] = CompactClone(row, 2),
                ["name"] = CompactClone(row, 3),
                ["session_type"] = CompactClone(row, 4),
                ["session_date"] = CompactClone(row, 5),
                ["categories"] = new JsonObject
                {
                    ["id"] = CompactClone(row, 2),
                    ["code"] = CompactClone(row, 10)
                },
                ["rounds"] = round
            };
            sessions.Add(session.DeepClone().AsObject());
            sessionsById[id] = session;
        }

        var ridersById = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in LastRowsById(packages, "d"))
        {
            var id = CompactString(row, 0);
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var rider = new JsonObject
            {
                ["id"] = CompactClone(row, 0),
                ["uci_id"] = CompactClone(row, 1),
                ["first_name"] = CompactClone(row, 2),
                ["last_name"] = CompactClone(row, 3),
                ["nation"] = CompactClone(row, 4),
                ["profile_image_url"] = CompactClone(row, 5),
                ["uci_code"] = CompactClone(row, 8),
                ["birth_date"] = CompactClone(row, 9),
                ["has_injury"] = CompactClone(row, 10),
                ["gender"] = CompactClone(row, 12)
            };
            var teamId = CompactString(row, 6);
            if (!string.IsNullOrWhiteSpace(teamId))
            {
                rider["teams"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = CompactClone(row, 6),
                        ["name"] = CompactClone(row, 7),
                        ["code"] = CompactClone(row, 11)
                    }
                };
            }
            else
            {
                rider["teams"] = new JsonArray();
            }

            ridersById[id] = rider;
        }

        var teamsById = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in LastRowsById(packages, "t"))
        {
            var id = CompactString(row, 0);
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            teamsById[id] = new JsonObject
            {
                ["id"] = CompactClone(row, 0),
                ["name"] = CompactClone(row, 1),
                ["code"] = CompactClone(row, 2)
            };
        }

        var results = new List<RankingReadResult>();
        var pointsByResultId = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in CompactRows(packages, "r"))
        {
            var resultId = CompactString(row, 0);
            var riderId = CompactString(row, 1);
            var sessionId = CompactString(row, 2);
            if (string.IsNullOrWhiteSpace(resultId) ||
                string.IsNullOrWhiteSpace(riderId) ||
                string.IsNullOrWhiteSpace(sessionId))
            {
                continue;
            }

            ridersById.TryGetValue(riderId, out var rider);
            sessionsById.TryGetValue(sessionId, out var session);
            var currentTeam = rider?.Array("teams")?.OfType<JsonObject>().FirstOrDefault();
            var resultTeamId = CompactString(row, 9);
            JsonObject? team;
            if (string.IsNullOrWhiteSpace(resultTeamId))
            {
                team = currentTeam;
            }
            else if (teamsById.TryGetValue(resultTeamId, out var compactTeam))
            {
                team = compactTeam;
            }
            else if (!string.IsNullOrWhiteSpace(CompactString(row, 10)) ||
                !string.IsNullOrWhiteSpace(CompactString(row, 11)))
            {
                team = new JsonObject
                {
                    ["id"] = CompactClone(row, 9),
                    ["name"] = CompactClone(row, 10),
                    ["code"] = CompactClone(row, 11)
                };
            }
            else if (teamsById.Count == 0)
            {
                team = currentTeam;
            }
            else
            {
                team = new JsonObject
                {
                    ["id"] = CompactClone(row, 9),
                    ["name"] = CompactClone(row, 10),
                    ["code"] = CompactClone(row, 11)
                };
            }
            var awardedPoints = CompactDecimal(row, 8) ?? 0m;
            results.Add(new RankingReadResult(new JsonObject
            {
                ["id"] = CompactClone(row, 0),
                ["rider_id"] = CompactClone(row, 1),
                ["session_id"] = CompactClone(row, 2),
                ["race_time_ms"] = CompactClone(row, 3),
                ["top_speed"] = CompactClone(row, 4),
                ["position"] = CompactClone(row, 5),
                ["is_classified"] = CompactClone(row, 6),
                ["status"] = CompactClone(row, 7),
                ["team_id"] = CompactClone(row, 9),
                ["awarded_points"] = awardedPoints
            }, rider, team, session));

            pointsByResultId[resultId] = awardedPoints;
        }

        var compactFieldRows = CompactRows(packages, "f").ToArray();
        IReadOnlyList<RankingReadResult> fieldResults = compactFieldRows.Length == 0
            ? results
            : compactFieldRows
                .Select(row =>
                {
                    var sessionId = CompactString(row, 2);
                    sessionsById.TryGetValue(sessionId ?? string.Empty, out var session);
                    return new RankingReadResult(new JsonObject
                    {
                        ["id"] = CompactClone(row, 0),
                        ["rider_id"] = CompactClone(row, 1),
                        ["session_id"] = CompactClone(row, 2),
                        ["race_time_ms"] = CompactClone(row, 3),
                        ["top_speed"] = CompactClone(row, 4),
                        ["position"] = CompactClone(row, 5),
                        ["is_classified"] = CompactClone(row, 6),
                        ["status"] = CompactClone(row, 7)
                    }, null, null, session, IncludeRiderMetadata: false);
                })
                .ToArray();

        var splits = CompactRows(packages, "x")
            .Select(row => new JsonObject
            {
                ["result_id"] = CompactClone(row, 0),
                ["split_index"] = CompactClone(row, 1),
                ["race_time"] = CompactClone(row, 2),
                ["speed"] = CompactClone(row, 3),
                ["split_rank"] = CompactClone(row, 4),
                ["sector_rank"] = CompactClone(row, 5)
            })
            .ToArray();

        return new RankingReadData(
            sessions,
            results,
            fieldResults,
            splits,
            pointsByResultId);
    }

    internal static RiderDetailDataBundle ParseRiderDetailDataBundle(JsonNode? response)
    {
        var root = response as JsonObject ??
            (response as JsonArray)?.OfType<JsonObject>().FirstOrDefault() ??
            throw new ApiException(
                StatusCodes.Status502BadGateway,
                "RIDER_DETAIL_DATA_INVALID",
                "Supabase returned an invalid rider-detail data package.");
        return ParseRiderDetailDataBundle([root]);
    }

    internal static RiderDetailDataBundle ParseRiderDetailDataBundle(IReadOnlyList<JsonObject> packages)
    {
        var read = ParsePreparedRankingRead(packages);
        return new RiderDetailDataBundle(read.Data.ToLegacy(), read.PaceSessions);
    }

    internal static (RankingReadData Data, IReadOnlyList<PacePrecomputedSessionData> PaceSessions) ParsePreparedRankingRead(IReadOnlyList<JsonObject> packages)
    {
        var data = ParseRankingReadData(packages);
        if (packages.All(package => package.Int("v") == 3))
        {
            var measurements = CompactRows(packages, "ix").Select(row => new JsonObject
            {
                ["result_id"] = CompactClone(row, 0),
                ["split_index"] = CompactClone(row, 1),
                ["race_time"] = CompactClone(row, 2)
            }).ToArray();
            return (data, PaceService.PrepareInputSessions(data, measurements));
        }
        var resultsById = data.Results
            .Where(result => !string.IsNullOrWhiteSpace(result.String("id")))
            .ToDictionary(
                result => result.String("id")!,
                result => result,
                StringComparer.OrdinalIgnoreCase);
        var compactSessionStatistics = CompactRows(packages, "h")
            .Where(row => !string.IsNullOrWhiteSpace(CompactString(row, 0)))
            .ToDictionary(
                row => CompactString(row, 0)!,
                row => row,
                StringComparer.OrdinalIgnoreCase);
        var compactSectorStatistics = CompactRows(packages, "g")
            .Where(row =>
                !string.IsNullOrWhiteSpace(CompactString(row, 0)) &&
                CompactInt(row, 1).HasValue)
            .ToDictionary(
                row => $"{CompactString(row, 0)}\u001f{CompactInt(row, 1)}",
                row => row,
                StringComparer.OrdinalIgnoreCase);
        var hasCompactStatistics = compactSessionStatistics.Count > 0 ||
            compactSectorStatistics.Count > 0;
        var sectorRowsByResultId = CompactRows(packages, "q")
            .Where(row => !string.IsNullOrWhiteSpace(CompactString(row, 0)))
            .GroupBy(row => CompactString(row, 0)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(row => CompactInt(row, 1) ?? int.MaxValue).ToArray(),
                StringComparer.OrdinalIgnoreCase);

        var sessions = new List<PacePrecomputedSessionData>();
        foreach (var aggregateRow in CompactRows(packages, "a")
            .Where(row =>
                !string.IsNullOrWhiteSpace(CompactString(row, 0)) &&
                !string.IsNullOrWhiteSpace(CompactString(row, 1))))
        {
            var resultId = CompactString(aggregateRow, 0)!;
            if (!resultsById.TryGetValue(resultId, out var result) ||
                result.Object("sessions") is not { } session ||
                string.IsNullOrWhiteSpace(result.String("rider_id")))
            {
                continue;
            }

            var sectorRows = sectorRowsByResultId.GetValueOrDefault(
                resultId,
                []);
            var sectors = sectorRows
                .Select(row => new PacePrecomputedSector(
                    CompactInt(row, 1) ?? 0,
                    CompactDouble(row, 2),
                    CompactInt(row, 3),
                    hasCompactStatistics && compactSectorStatistics.TryGetValue(
                        $"{CompactString(aggregateRow, 1)}\u001f{CompactInt(row, 1)}",
                        out var compactStatistics)
                            ? ParseFieldSummary(compactStatistics, 2)!
                            : ParseFieldSummary(row, 4)!))
                .ToArray();
            var cumulativeStatistics = sectorRows
                .Select(row =>
                    hasCompactStatistics && compactSectorStatistics.TryGetValue(
                        $"{CompactString(aggregateRow, 1)}\u001f{CompactInt(row, 1)}",
                        out var compactStatistics)
                            ? ParseFieldSummary(compactStatistics, 9)
                            : ParseFieldSummary(row, 11))
                .ToArray();
            var allSectorTimes = sectors
                .OrderBy(sector => sector.SectorIndex)
                .Select(sector => sector.TimeSeconds)
                .ToArray();
            var lastRecordedSector = Array.FindLastIndex(
                allSectorTimes,
                time => time is > 0 && double.IsFinite(time.Value));
            var sectorTimes = lastRecordedSector < 0
                ? []
                : allSectorTimes.Take(lastRecordedSector + 1).ToArray();
            var raceTimeMs = result.Int("race_time_ms");
            var status = result.String("status");
            var isClassified =
                (result.Bool("is_classified") == true || result.Int("position") is > 0) &&
                !string.Equals(status, "DNF", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(status, "DNS", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(status, "DSQ", StringComparison.OrdinalIgnoreCase);

            compactSessionStatistics.TryGetValue(
                CompactString(aggregateRow, 1)!,
                out var sessionStatistics);
            sessions.Add(new PacePrecomputedSessionData(
                SessionId: CompactString(aggregateRow, 1)!,
                CourseKey: PaceService.BuildCourseKey(result),
                CategoryId: session.String("category_id") ?? session.Object("categories")?.String("id"),
                SessionType: PaceService.ParseSessionType(
                    session.String("session_type"),
                    session.String("name")),
                Run: new PaceRiderRun(
                    RiderId: result.String("rider_id")!,
                    SectorTimes: sectorTimes,
                    TopSpeed: result.Decimal("top_speed") is { } speed
                        ? (double)speed
                        : null,
                    FinishRank: result.Int("position"),
                    FinishTimeSeconds: raceTimeMs is > 0
                        ? raceTimeMs.Value / 1000.0
                        : null,
                    IsClassified: isClassified,
                    Status: status),
                ClassifiedFieldSize: CompactInt(aggregateRow, 2) ?? 0,
                Sectors: sectors,
                SpeedStatistics: sessionStatistics is null
                    ? ParseFieldSummary(aggregateRow, 4)
                    : ParseFieldSummary(sessionStatistics, 1),
                SpeedRank: CompactInt(aggregateRow, 3),
                FinishStatistics: sessionStatistics is null
                    ? ParseFieldSummary(aggregateRow, 11)
                    : ParseFieldSummary(sessionStatistics, 8),
                CumulativeStatistics: cumulativeStatistics,
                CompetitionClassPriority:
                    session.Object("rounds")?.Int("competition_class_priority"),
                SessionDate: ParseTimestamp(
                    session.String("session_date") ??
                    session.Object("rounds")?.String("start_date"))));
        }

        // A detail response includes only one rider's public rows. Its small `i`
        // population carries the classified field needed to prepare Pace fairly,
        // without fetching rival identities/splits or duplicating the detector in SQL.
        var cohort = new Dictionary<(string Session, string Rider), PacePrecomputedSessionData>();
        foreach (var s in sessions) cohort[(s.SessionId, s.Run.RiderId)] = s;
        var templates = sessions.GroupBy(s => s.SessionId).ToDictionary(g => g.Key, g => g.First());
        foreach (var row in CompactRows(packages, "i"))
        {
            var sessionId = CompactString(row, 0);
            var riderId = CompactString(row, 1);
            if (sessionId is null || riderId is null || !templates.TryGetValue(sessionId, out var template) ||
                cohort.ContainsKey((sessionId, riderId)) || row[3] is not JsonArray timings) continue;
            var byIndex = timings.OfType<JsonArray>().ToDictionary(s => CompactInt(s, 0) ?? -1);
            var sectors = template.Sectors.Select(s => byIndex.TryGetValue(s.SectorIndex, out var timing)
                ? s with { TimeSeconds = CompactDouble(timing, 1), Rank = CompactInt(timing, 2) }
                : s with { TimeSeconds = null, Rank = null }).ToArray();
            cohort[(sessionId, riderId)] = template with
            {
                Run = new PaceRiderRun(riderId, sectors.Select(s => s.TimeSeconds).ToArray(),
                    FinishTimeSeconds: CompactDouble(row, 2)),
                Sectors = sectors,
                PaceFinish = null
            };
        }
        var savedFinishes = CompactRows(packages, "pf")
            .ToDictionary(r => (CompactString(r, 0)!, CompactString(r, 1)!),
                r => ParseFieldSummary(r, 4) is { } summary
                    ? new PaceFinishReference(CompactDouble(r, 2), CompactDouble(r, 3) ?? 0, summary)
                    : null);
        foreach (var key in cohort.Keys)
            if (savedFinishes.TryGetValue(key, out var finish))
                cohort[key] = cohort[key] with { PaceFinish = finish };
        var prepared = new DownhillPaceCalculator().PrepareField(cohort.Values.ToArray())
            .ToDictionary(s => (s.SessionId, s.Run.RiderId));
        return (data,
            sessions.Select(s => prepared[(s.SessionId, s.Run.RiderId)]).ToArray());
    }

    internal static bool HasCompleteInputMeasurements(JsonObject package)
    {
        if (package.Int("v") != 3 || package["r"] is not JsonArray results ||
            package["s"] is not JsonArray sessions || package["f"] is not JsonArray field ||
            package["ix"] is not JsonArray measurements || package["x"] is not JsonArray)
            return false;
        if (results.Any(row => row is not JsonArray { Count: >= 8 }) ||
            field.Any(row => row is not JsonArray { Count: >= 8 }) ||
            sessions.Any(row => row is not JsonArray { Count: >= 5 }) ||
            measurements.Any(row => row is not JsonArray { Count: >= 3 })) return false;
        var sessionIds = sessions.OfType<JsonArray>().Select(row => CompactString(row, 0)).ToHashSet();
        var fieldIds = field.OfType<JsonArray>().Select(row => CompactString(row, 0)).ToHashSet();
        if (sessionIds.Contains(null) || fieldIds.Contains(null) ||
            sessionIds.Count != sessions.Count || fieldIds.Count != field.Count ||
            results.OfType<JsonArray>().Any(row => CompactString(row, 0) is null || CompactString(row, 1) is null) ||
            field.OfType<JsonArray>().Any(row => CompactString(row, 1) is null)) return false;
        return results.OfType<JsonArray>().All(row => fieldIds.Contains(CompactString(row, 0))) &&
            field.OfType<JsonArray>().All(row => sessionIds.Contains(CompactString(row, 2))) &&
            measurements.OfType<JsonArray>().All(row => fieldIds.Contains(CompactString(row, 0)));
    }

    internal static bool HasCompletePreparedFinishes(JsonObject package)
    {
        if (package["r"] is not JsonArray results) return false;
        var resultRows = results.OfType<JsonArray>().ToArray();
        if (resultRows.Length != results.Count) return false;
        var rows = resultRows.ToDictionary(r => CompactString(r, 0)!);
        var aggregates = CompactRows([package], "a").ToArray();
        if (aggregates.Length != results.Count ||
            aggregates.Select(a => CompactString(a, 0)).Distinct().Count() != results.Count)
            return false;
        var finishes = CompactRows([package], "pf").ToArray();
        if (finishes.Length != results.Count || finishes.Any(row => !IsValidPreparedFinish(row))) return false;
        var keys = finishes.Select(row => (CompactString(row, 0), CompactString(row, 1))).ToHashSet();
        if (keys.Count != finishes.Length) return false;
        return aggregates.All(aggregate =>
            rows.TryGetValue(CompactString(aggregate, 0) ?? "", out var result) &&
            CompactString(aggregate, 1) == CompactString(result, 2) &&
            keys.Contains((CompactString(aggregate, 1), CompactString(result, 1))));
    }

    private static bool IsValidPreparedFinish(JsonArray row)
    {
        if (row.Count != 11 || CompactString(row, 0) is null || CompactString(row, 1) is null)
            return false;
        // No classified field/reference is a valid explicit absence, not a partial summary.
        if (row.Skip(2).All(value => value is null)) return true;
        if (CompactInt(row, 4) is not > 0) return false;
        for (var index = 2; index < row.Count; index++)
        {
            if (index is 2 or 9 or 10 && row[index] is null) continue;
            if (CompactDouble(row, index) is not { } value || !double.IsFinite(value)) return false;
        }
        return true;
    }

    private static PaceFieldSummary? ParseFieldSummary(JsonArray row, int offset)
    {
        var count = CompactInt(row, offset) ?? 0;
        var median = CompactDouble(row, offset + 1);
        var logMedian = CompactDouble(row, offset + 2);
        var logMadScale = CompactDouble(row, offset + 3);
        var linearMadScale = CompactDouble(row, offset + 4);
        if (count <= 0 ||
            !median.HasValue ||
            !logMedian.HasValue ||
            !logMadScale.HasValue ||
            !linearMadScale.HasValue)
        {
            return null;
        }

        return new PaceFieldSummary(
            count,
            median.Value,
            logMedian.Value,
            logMadScale.Value,
            linearMadScale.Value,
            CompactDouble(row, offset + 5),
            CompactDouble(row, offset + 6));
    }

    private static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;

    private static IEnumerable<JsonArray> CompactRows(IReadOnlyList<JsonObject> packages, string property) =>
        packages.SelectMany(root => root[property] is JsonArray rows
            ? rows.OfType<JsonArray>()
            : []);

    // Identity dictionaries have always used the last row across the whole package set.
    // Resolve duplicates before allocating expanded JSON, without mutating cached payloads.
    private static IEnumerable<JsonArray> LastRowsById(IReadOnlyList<JsonObject> packages, string property)
    {
        var rows = new Dictionary<string, JsonArray>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in CompactRows(packages, property))
            if (CompactString(row, 0) is { } id && !string.IsNullOrWhiteSpace(id))
                rows[id] = row;
        return rows.Values;
    }

    private static JsonNode? CompactClone(JsonArray row, int index) =>
        index < row.Count ? row[index]?.DeepClone() : null;

    private static string? CompactString(JsonArray row, int index) =>
        index < row.Count ? row[index]?.ToString() : null;

    private static decimal? CompactDecimal(JsonArray row, int index) =>
        decimal.TryParse(
            CompactString(row, index),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var value)
                ? value
                : null;

    private static double? CompactDouble(JsonArray row, int index) =>
        double.TryParse(
            CompactString(row, index),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
                ? value
                : null;

    private static int? CompactInt(JsonArray row, int index) =>
        int.TryParse(
            CompactString(row, index),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
                ? value
                : null;

    private static long? CompactLong(JsonArray row, int index) =>
        long.TryParse(
            CompactString(row, index),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
                ? value
                : null;

}
