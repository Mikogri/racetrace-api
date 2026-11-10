using System.Text.Json.Nodes;
using RaceTrace.Api.Features.Results;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Features.Metrics;

/// <summary>Maps stored results and cumulative split measurements to calculator run inputs without I/O.</summary>
internal static class PaceRunMapper
{
    internal static int? CompetitionClassPriority(JsonObject? session)
    {
        var round =
            session?.Object("rounds") ?? session?.Object("round_categories")?.Object("rounds");
        return round?.Int("competition_class_priority")
            ?? round?.Object("competition_class")?.Int("priority")
            ?? round?.Object("competition_levels")?.Int("priority");
    }

    internal static PaceRiderRun BuildRun(
        JsonObject result,
        IReadOnlyDictionary<string, IReadOnlyList<JsonObject>> splitsByResultId,
        SessionSplitLayout splitLayout
    ) => BuildRun(RankingReadResult.FromLegacy(result), splitsByResultId, splitLayout);

    internal static PaceRiderRun BuildRun(
        RankingReadResult result,
        IReadOnlyDictionary<string, IReadOnlyList<JsonObject>> splitsByResultId,
        SessionSplitLayout splitLayout
    )
    {
        var resultId = result.String("id") ?? string.Empty;
        splitsByResultId.TryGetValue(resultId, out var resultSplits);
        var sectorTimes = BuildSectorTimes(
            resultSplits ?? [],
            splitLayout.MaximumSplitIndex,
            result.Int("race_time_ms")
        );
        var finishTimeMs = result.Int("race_time_ms");

        return new PaceRiderRun(
            RiderId: result.String("rider_id")!,
            SectorTimes: sectorTimes,
            TopSpeed: result.Decimal("top_speed") is { } speed ? (double)speed : null,
            FinishRank: result.Int("position"),
            FinishTimeSeconds: finishTimeMs is > 0 ? finishTimeMs.Value / 1000.0 : null,
            IsClassified: IsClassifiedRun(result),
            Status: result.String("status")
        );
    }

    internal static bool IsClassifiedRun(JsonObject result) =>
        IsClassifiedRun(RankingReadResult.FromLegacy(result));

    internal static bool IsClassifiedRun(RankingReadResult result)
    {
        var status = result.String("status");
        return (result.Bool("is_classified") == true || result.Int("position") is > 0)
            && !string.Equals(status, "DNF", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(status, "DNS", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(status, "DSQ", StringComparison.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<double?> BuildSectorTimes(
        IReadOnlyList<JsonObject> splits,
        int maximumSplitIndex,
        int? finishTimeMs = null
    )
    {
        var latestSplitsByIndex = splits
            .Where(split => split.Int("split_index") is > 0)
            .GroupBy(split => split.Int("split_index")!.Value)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(split => split.Decimal("id") ?? 0).First()
            );
        maximumSplitIndex = Math.Max(
            maximumSplitIndex,
            latestSplitsByIndex.Keys.DefaultIfEmpty(0).Max()
        );

        var sectors = new List<double?>(maximumSplitIndex);
        int? previousTimeMs = 0;
        for (var splitIndex = 1; splitIndex <= maximumSplitIndex; splitIndex++)
        {
            var cumulativeTimeMs = latestSplitsByIndex
                .GetValueOrDefault(splitIndex)
                ?.Int("race_time");
            if (
                cumulativeTimeMs is > 0
                && previousTimeMs.HasValue
                && cumulativeTimeMs.Value > previousTimeMs.Value
            )
            {
                sectors.Add((cumulativeTimeMs.Value - previousTimeMs.Value) / 1000.0);
            }
            else
            {
                sectors.Add(null);
            }

            previousTimeMs = cumulativeTimeMs is > 0 ? cumulativeTimeMs.Value : null;
        }

        if (
            finishTimeMs is > 0
            && previousTimeMs is > 0
            && finishTimeMs.Value > previousTimeMs.Value
        )
        {
            sectors.Add((finishTimeMs.Value - previousTimeMs.Value) / 1000.0);
        }

        return sectors;
    }

    internal static Dictionary<string, SessionSplitLayout> BuildSessionSplitLayouts(
        IReadOnlyList<JsonObject> fieldResults,
        IReadOnlyDictionary<string, IReadOnlyList<JsonObject>> splitsByResultId
    )
    {
        return fieldResults
            .Where(result => !string.IsNullOrWhiteSpace(result.String("session_id")))
            .GroupBy(result => result.String("session_id")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var resultSplits = group
                        .SelectMany(result =>
                            splitsByResultId.GetValueOrDefault(
                                result.String("id") ?? string.Empty,
                                Array.Empty<JsonObject>()
                            )
                        )
                        .ToArray();
                    var maximumIndex = resultSplits
                        .Select(split => split.Int("split_index") ?? 0)
                        .DefaultIfEmpty(0)
                        .Max();
                    return new SessionSplitLayout(maximumIndex);
                },
                StringComparer.OrdinalIgnoreCase
            );
    }

    internal sealed record SessionSplitLayout(int MaximumSplitIndex)
    {
        public static SessionSplitLayout Empty { get; } = new(0);
    }

    internal static PaceSessionType ParseSessionType(string? sessionType, string? sessionName)
    {
        var type = ResultEmbedMapping.NormalizeSessionType(sessionType ?? string.Empty);
        var name = (sessionName ?? string.Empty).Trim().ToUpperInvariant();

        if (type == "SEMI" || name.Contains("SEMI", StringComparison.Ordinal))
        {
            return PaceSessionType.SemiFinal;
        }

        if (type == "FINAL" || name.Contains("FINAL", StringComparison.Ordinal))
        {
            return PaceSessionType.Final;
        }

        if (type == "Q1" || name == "Q1" || name.Contains("QUALIFYING 1", StringComparison.Ordinal))
        {
            return PaceSessionType.Qualifying1;
        }

        if (type == "Q2" || name == "Q2" || name.Contains("QUALIFYING 2", StringComparison.Ordinal))
        {
            return PaceSessionType.Qualifying2;
        }

        if (
            type is "QUALI" or "QUALIFYING" or "QUALIFICATION"
            || name.Contains("QUAL", StringComparison.Ordinal)
        )
        {
            return PaceSessionType.Qualifying;
        }

        if (type == "SEEDING" || name.Contains("SEED", StringComparison.Ordinal))
        {
            return PaceSessionType.Seeding;
        }

        if (
            type == "TIMED_TRAINING"
            || (
                name.Contains("TIMED", StringComparison.Ordinal)
                && name.Contains("TRAIN", StringComparison.Ordinal)
            )
        )
        {
            return PaceSessionType.TimedTraining;
        }

        return PaceSessionType.Training;
    }

    internal static string BuildCourseKey(JsonObject result) =>
        BuildCourseKey(RankingReadResult.FromLegacy(result));

    internal static string BuildCourseKey(RankingReadResult result)
    {
        var session = result.Object("sessions");
        var round =
            session?.Object("rounds") ?? session?.Object("round_categories")?.Object("rounds");
        var category =
            session?.Object("categories")
            ?? session?.Object("round_categories")?.Object("categories");
        var roundId = session?.String("round_id") ?? round?.String("id");
        var categoryId = session?.String("category_id") ?? category?.String("id");

        if (!string.IsNullOrWhiteSpace(roundId) && !string.IsNullOrWhiteSpace(categoryId))
        {
            return $"round:{roundId}:category:{categoryId}";
        }

        var trackId = round?.String("track_id") ?? round?.Object("tracks")?.String("id");
        var seasonYear = round?.String("season_year");
        if (
            !string.IsNullOrWhiteSpace(trackId)
            && !string.IsNullOrWhiteSpace(seasonYear)
            && !string.IsNullOrWhiteSpace(categoryId)
        )
        {
            return $"track:{trackId}:season:{seasonYear}:category:{categoryId}";
        }

        return $"session:{result.String("session_id") ?? session?.String("id") ?? result.String("id") ?? "unknown"}";
    }
}
