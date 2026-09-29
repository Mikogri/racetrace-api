using System.Text.Json.Nodes;
using UCI_Stats_API.Models;
using UCI_Stats_API.Services;
using static UCI_Stats_API.Features.Results.SessionResultMapper;

namespace UCI_Stats_API.Features.Results;

/// <summary>Projects loaded timings into sectors, ranks, reference gaps and Perfect Run without performing I/O.</summary>
internal static class ResultTimingProjection
{
    private const string TimingModeSectors = "sectors";
    private const string TimingModeSplits = "splits";

    internal static Dictionary<string, IReadOnlyList<SectorCalculation>> BuildSectorsByResultId(
        IReadOnlyList<JsonObject> splits,
        IReadOnlyList<JsonObject>? results = null,
        bool includeFinishSector = false)
    {
        var sectorsByResultId = splits
            .GroupBy(split => split.String("result_id") ?? string.Empty)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<SectorCalculation>)BuildResultSectors(group.ToArray()),
                StringComparer.OrdinalIgnoreCase);

        if (!includeFinishSector || results is null)
        {
            return sectorsByResultId;
        }

        var maximumSplitIndex = splits
            .Select(split => split.Int("split_index") ?? 0)
            .DefaultIfEmpty(0)
            .Max();
        if (maximumSplitIndex <= 0)
        {
            return sectorsByResultId;
        }

        foreach (var result in results)
        {
            // Non-finishers may carry a partial clock in race_time_ms, not a finish time.
            if (result.String("status")?.Trim().ToUpperInvariant() is "DNF" or "DNS" or "DSQ")
            {
                continue;
            }

            var resultId = result.String("id");
            if (string.IsNullOrWhiteSpace(resultId) ||
                !sectorsByResultId.TryGetValue(resultId, out var resultSectors))
            {
                continue;
            }

            var tail = resultSectors.FirstOrDefault(sector => sector.SplitIndex == maximumSplitIndex);
            var finishTimeMs = result.Int("race_time_ms");
            if (tail?.RaceTimeMs is not > 0 ||
                finishTimeMs is not > 0 ||
                finishTimeMs.Value <= tail.RaceTimeMs.Value)
            {
                continue;
            }

            sectorsByResultId[resultId] = resultSectors
                .Append(new SectorCalculation(
                    resultId,
                    null,
                    maximumSplitIndex + 1,
                    finishTimeMs,
                    finishTimeMs.Value - tail.RaceTimeMs.Value,
                    null))
                .ToArray();
        }

        return sectorsByResultId;
    }

    internal static int CountSessionSectors(
        IReadOnlyList<JsonObject> results,
        IReadOnlyList<JsonObject> splits)
    {
        var splitCount = splits
            .Select(split => split.Int("split_index") ?? 0)
            .DefaultIfEmpty(0)
            .Max();
        if (splitCount == 0)
        {
            return 0;
        }

        return BuildSectorsByResultId(splits, results, includeFinishSector: true)
            .Values
            .SelectMany(value => value)
            .Select(sector => sector.SplitIndex)
            .DefaultIfEmpty(0)
            .Max();
    }

    internal static IReadOnlyList<SectorCalculation> BuildResultSectors(IReadOnlyList<JsonObject> splits)
    {
        var sectors = new List<SectorCalculation>();
        int? previousRaceTimeMs = 0;
        foreach (var split in splits.OrderBy(row => row.Int("split_index") ?? 0))
        {
            var resultId = split.String("result_id");
            var splitIndex = split.Int("split_index");
            if (string.IsNullOrWhiteSpace(resultId) || splitIndex is null)
            {
                continue;
            }

            var raceTimeMs = split.Int("race_time");
            int? sectorTimeMs = null;
            if (raceTimeMs is not null && previousRaceTimeMs is not null)
            {
                var calculatedSectorTimeMs = raceTimeMs.Value - previousRaceTimeMs.Value;
                sectorTimeMs = calculatedSectorTimeMs >= 0 ? calculatedSectorTimeMs : null;
            }

            sectors.Add(new SectorCalculation(
                resultId,
                split.String("id"),
                splitIndex.Value,
                raceTimeMs,
                sectorTimeMs,
                split.Decimal("speed")));

            previousRaceTimeMs = raceTimeMs;
        }

        return sectors;
    }

    internal static Dictionary<(int SplitIndex, string ResultId), int> BuildSectorPositions(
        IEnumerable<SectorCalculation> sectors,
        IReadOnlyDictionary<string, int>? statusOrders = null)
    {
        var positions = new Dictionary<(int SplitIndex, string ResultId), int>();
        foreach (var group in sectors
            .Where(sector => sector.SectorTimeMs is > 0)
            .GroupBy(sector => sector.SplitIndex))
        {
            (int StatusGroup, int TimeMs)? previousRankKey = null;
            var position = 0;
            var rowNumber = 0;
            foreach (var sector in group
                .OrderBy(item => TimingStatusGroup(item.ResultId, statusOrders))
                .ThenBy(item => item.SectorTimeMs)
                .ThenBy(item => item.ResultId))
            {
                rowNumber++;
                var rankKey = (TimingStatusGroup(sector.ResultId, statusOrders), sector.SectorTimeMs!.Value);
                if (rankKey != previousRankKey)
                {
                    position = rowNumber;
                    previousRankKey = rankKey;
                }

                positions[(sector.SplitIndex, sector.ResultId)] = position;
            }
        }

        return positions;
    }

    internal static JsonObject ToSectorJson(
        SectorCalculation sector,
        IReadOnlyDictionary<int, TimingReference> sectorReferences,
        IReadOnlyDictionary<int, int> leaderSectorTimes,
        IReadOnlyDictionary<(int SplitIndex, string ResultId), int> sectorPositions)
    {
        var reference = sectorReferences.GetValueOrDefault(sector.SplitIndex);
        var bestSectorTimeMs = reference?.BestTimeMs;
        var leaderSectorTimeMs = leaderSectorTimes.GetValueOrDefault(sector.SplitIndex);
        var gapToLeaderMs = CalculateGapToLeader(sector.SectorTimeMs, leaderSectorTimeMs);
        var sectorPosition = sectorPositions.TryGetValue((sector.SplitIndex, sector.ResultId), out var position)
            ? position
            : (int?)null;

        return new JsonObject
        {
            ["time_split_id"] = sector.TimeSplitId,
            ["split_index"] = sector.SplitIndex,
            ["race_time_ms"] = sector.RaceTimeMs,
            ["sector_time_ms"] = sector.SectorTimeMs,
            ["best_sector_time_ms"] = bestSectorTimeMs,
            ["sector_gap_to_best_ms"] = gapToLeaderMs,
            ["leader_sector_time_ms"] = leaderSectorTimeMs is > 0 ? leaderSectorTimeMs : null,
            ["sector_gap_to_leader_ms"] = gapToLeaderMs,
            ["sector_position"] = sectorPosition,
            ["speed"] = sector.Speed
        };
    }

    internal static JsonObject ToSplitJson(
        SectorCalculation split,
        IReadOnlyDictionary<int, TimingReference> splitReferences,
        IReadOnlyDictionary<int, int> leaderSplitTimes,
        IReadOnlyDictionary<(int SplitIndex, string ResultId), int> splitPositions)
    {
        var reference = splitReferences.GetValueOrDefault(split.SplitIndex);
        var bestSplitTimeMs = reference?.BestTimeMs;
        var leaderSplitTimeMs = leaderSplitTimes.GetValueOrDefault(split.SplitIndex);
        var gapToLeaderMs = CalculateGapToLeader(split.RaceTimeMs, leaderSplitTimeMs);

        return new JsonObject
        {
            ["time_split_id"] = split.TimeSplitId,
            ["split_index"] = split.SplitIndex,
            ["race_time_ms"] = split.RaceTimeMs,
            ["best_split_time_ms"] = bestSplitTimeMs,
            ["split_gap_to_best_ms"] = gapToLeaderMs,
            ["leader_split_time_ms"] = leaderSplitTimeMs is > 0 ? leaderSplitTimeMs : null,
            ["split_gap_to_leader_ms"] = gapToLeaderMs,
            ["split_position"] = splitPositions.TryGetValue((split.SplitIndex, split.ResultId), out var position)
                ? position
                : null,
            ["speed"] = split.Speed
        };
    }

    internal static Dictionary<(int SplitIndex, string ResultId), int> BuildSplitPositions(
        IEnumerable<SectorCalculation> splits,
        IReadOnlyDictionary<string, int>? statusOrders = null)
    {
        var positions = new Dictionary<(int SplitIndex, string ResultId), int>();
        foreach (var group in splits.Where(split => split.RaceTimeMs is > 0).GroupBy(split => split.SplitIndex))
        {
            (int StatusGroup, int TimeMs)? previousRankKey = null;
            var position = 0;
            var rowNumber = 0;
            foreach (var split in group
                .OrderBy(item => TimingStatusGroup(item.ResultId, statusOrders))
                .ThenBy(item => item.RaceTimeMs)
                .ThenBy(item => item.ResultId, StringComparer.Ordinal))
            {
                rowNumber++;
                var rankKey = (TimingStatusGroup(split.ResultId, statusOrders), split.RaceTimeMs!.Value);
                if (rankKey != previousRankKey)
                {
                    position = rowNumber;
                    previousRankKey = rankKey;
                }

                positions[(split.SplitIndex, split.ResultId)] = position;
            }
        }

        return positions;
    }

    internal static int TimingStatusGroup(
        string resultId,
        IReadOnlyDictionary<string, int>? statusOrders) =>
        statusOrders is not null && statusOrders.GetValueOrDefault(resultId) > 0 ? 1 : 0;

    internal static Dictionary<int, TimingReference> BuildTimingReferences(
        IEnumerable<SectorCalculation> timings,
        Func<SectorCalculation, int?> timeSelector)
    {
        return timings
            .Select(timing => new
            {
                timing.SplitIndex,
                TimeMs = timeSelector(timing)
            })
            .Where(timing => timing.TimeMs is > 0)
            .GroupBy(timing => timing.SplitIndex)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var best = group
                        .Select(timing => timing.TimeMs!.Value)
                        .Min();
                    return new TimingReference(best);
                });
    }

    internal static Dictionary<int, int> BuildLeaderTimes(
        IReadOnlyList<JsonObject> results,
        IEnumerable<SectorCalculation> timings,
        Func<SectorCalculation, int?> timeSelector)
    {
        var leaderResultId = results
            .Where(IsCompletedResult)
            .Where(result => !string.IsNullOrWhiteSpace(result.String("id")))
            .OrderBy(result => result.Int("race_time_ms") ?? int.MaxValue)
            .ThenBy(result => result.Int("position") ?? int.MaxValue)
            .Select(result => result.String("id"))
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(leaderResultId))
        {
            return [];
        }

        return timings
            .Where(timing => string.Equals(
                timing.ResultId,
                leaderResultId,
                StringComparison.OrdinalIgnoreCase))
            .Select(timing => new
            {
                timing.SplitIndex,
                TimeMs = timeSelector(timing)
            })
            .Where(timing => timing.TimeMs is > 0)
            .GroupBy(timing => timing.SplitIndex)
            .ToDictionary(
                group => group.Key,
                group => group.Last().TimeMs!.Value);
    }

    internal static int? CalculateGapToLeader(int? timeMs, int leaderTimeMs)
    {
        return timeMs is > 0 && leaderTimeMs > 0
            ? timeMs.Value - leaderTimeMs
            : null;
    }

    internal static SessionResultDto? BuildPerfectRun(
        IReadOnlyList<JsonObject> results,
        IReadOnlyList<JsonObject> splits,
        string timeView,
        decimal? trackLengthM = null)
    {
        var eligibleResultIds = results
            .Where(IsEligiblePerfectRunResult)
            .Select(result => result.String("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var eligibleResults = results
            .Where(result => eligibleResultIds.Contains(result.String("id") ?? string.Empty))
            .ToArray();
        var eligibleSplits = splits
            .Where(split => eligibleResultIds.Contains(split.String("result_id") ?? string.Empty))
            .ToArray();
        var maximumSplitIndex = eligibleSplits
            .Select(split => split.Int("split_index") ?? 0)
            .DefaultIfEmpty(0)
            .Max();
        var sectorsByResultId = BuildSectorsByResultId(
            eligibleSplits,
            eligibleResults,
            includeFinishSector: true);
        var completedResultIds = eligibleResults
            .Where(IsCompletedResult)
            .Select(result => result.String("id"))
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allSectors = sectorsByResultId.Values
            .SelectMany(sectors => sectors)
            .Where(sector =>
                sector.SplitIndex <= maximumSplitIndex ||
                completedResultIds.Contains(sector.ResultId))
            .ToArray();
        var sectorReferences = BuildTimingReferences(
            allSectors,
            sector => sector.SectorTimeMs);
        if (sectorReferences.Count == 0)
        {
            return null;
        }

        var leaderSectorTimes = BuildLeaderTimes(
            results,
            allSectors,
            sector => sector.SectorTimeMs);
        var leaderSplitTimes = BuildLeaderTimes(
            results,
            allSectors.Where(sector => sector.SplitIndex <= maximumSplitIndex),
            split => split.RaceTimeMs);
        var perfectTimeMs = 0;
        var times = new List<ResultTimeDto>(
            timeView == TimingModeSectors ? sectorReferences.Count : maximumSplitIndex);
        foreach (var (index, sectorReference) in sectorReferences.OrderBy(pair => pair.Key))
        {
            perfectTimeMs += sectorReference.BestTimeMs;
            if (timeView == TimingModeSplits && index > maximumSplitIndex)
            {
                continue;
            }

            var displayedTimeMs = timeView == TimingModeSectors
                ? sectorReference.BestTimeMs
                : perfectTimeMs;
            var gapMs = timeView == TimingModeSectors
                ? CalculateGapToLeader(
                    sectorReference.BestTimeMs,
                    leaderSectorTimes.GetValueOrDefault(index))
                : CalculateGapToLeader(
                    perfectTimeMs,
                    leaderSplitTimes.GetValueOrDefault(index));
            times.Add(new ResultTimeDto(
                index,
                $"{(timeView == TimingModeSectors ? "Sector" : "Split")} {index}",
                Seconds(displayedTimeMs),
                Seconds(gapMs),
                1));
        }

        var bestFinishTimeMs = results
            .Where(IsCompletedResult)
            .Select(result => result.Int("race_time_ms"))
            .Where(time => time is > 0)
            .Min();
        var finalGapMs = bestFinishTimeMs is > 0
            ? perfectTimeMs - bestFinishTimeMs.Value
            : (int?)null;

        return new SessionResultDto(
            null,
            null,
            null,
            null,
            null,
            null,
            "Perfect Run",
            null,
            null,
            null,
            "perfect_run",
            null,
            CalculateAverageSpeedKmh(trackLengthM, perfectTimeMs),
            Seconds(perfectTimeMs),
            Seconds(finalGapMs),
            0m,
            times)
        {
            IsPerfectRun = true
        };
    }

    internal static bool IsEligiblePerfectRunResult(JsonObject result)
    {
        var status = result.String("status");
        return !string.Equals(status, "DNS", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "DSQ", StringComparison.OrdinalIgnoreCase);
    }

    internal sealed record SectorCalculation(
        string ResultId,
        string? TimeSplitId,
        int SplitIndex,
        int? RaceTimeMs,
        int? SectorTimeMs,
        decimal? Speed);

    internal sealed record TimingReference(int BestTimeMs);
}
