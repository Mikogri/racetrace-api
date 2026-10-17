using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;
using static RaceTrace.Api.Features.Results.ResultTimingProjection;
using static RaceTrace.Api.Features.Results.SessionResultMapper;

namespace RaceTrace.Api.Features.Results;

/// <summary>Decorates loaded result rows using field-wide timing analytics; never fetches rows.</summary>
internal static class ResultTimingAnalytics
{
    private const string TimingModeSectors = "sectors";

    internal static void DecorateSectors(
        IReadOnlyList<JsonObject> pageResults,
        IReadOnlyList<JsonObject> sessionResults,
        IReadOnlyList<JsonObject> splits)
    {
        var sectorsByResultId = BuildSectorsByResultId(
            splits,
            sessionResults,
            includeFinishSector: true);
        var eligibleResultIds = sessionResults
            .Where(IsEligiblePerfectRunResult)
            .Select(result => result.String("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var eligibleSectors = sectorsByResultId
            .Where(pair => eligibleResultIds.Contains(pair.Key))
            .SelectMany(pair => pair.Value)
            .ToArray();
        var sectorReferences = BuildTimingReferences(
            eligibleSectors,
            sector => sector.SectorTimeMs);
        var allSectors = sectorsByResultId.Values.SelectMany(value => value).ToArray();
        var leaderSectorTimes = BuildLeaderTimes(
            sessionResults,
            allSectors,
            sector => sector.SectorTimeMs);
        var statusOrders = sessionResults
            .Where(result => !string.IsNullOrWhiteSpace(result.String("id")))
            .ToDictionary(result => result.String("id")!, ResultStatusOrder, StringComparer.OrdinalIgnoreCase);
        var sectorPositions = BuildSectorPositions(allSectors, statusOrders);

        foreach (var result in pageResults)
        {
            var resultId = result.String("id");
            if (resultId is null || !sectorsByResultId.TryGetValue(resultId, out var sectors))
            {
                result["sectors"] = new JsonArray();
                continue;
            }

            result["sectors"] = new JsonArray(sectors
                .Select(sector => ToSectorJson(
                    sector,
                    sectorReferences,
                    leaderSectorTimes,
                    sectorPositions))
                .ToArray());
        }
    }

    internal static void DecorateSplits(
        IReadOnlyList<JsonObject> pageResults,
        IReadOnlyList<JsonObject> sessionResults,
        IReadOnlyList<JsonObject> splits)
    {
        var splitsByResultId = BuildSectorsByResultId(splits);
        var eligibleResultIds = sessionResults
            .Where(IsEligiblePerfectRunResult)
            .Select(result => result.String("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var eligibleSplits = splitsByResultId
            .Where(pair => eligibleResultIds.Contains(pair.Key))
            .SelectMany(pair => pair.Value)
            .ToArray();
        var splitReferences = BuildTimingReferences(
            eligibleSplits,
            split => split.RaceTimeMs);
        var allSplits = splitsByResultId.Values.SelectMany(value => value).ToArray();
        var leaderSplitTimes = BuildLeaderTimes(
            sessionResults,
            allSplits,
            split => split.RaceTimeMs);
        var statusOrders = sessionResults
            .Where(result => !string.IsNullOrWhiteSpace(result.String("id")))
            .ToDictionary(result => result.String("id")!, ResultStatusOrder, StringComparer.OrdinalIgnoreCase);
        var splitPositions = BuildSplitPositions(allSplits, statusOrders);

        foreach (var result in pageResults)
        {
            var resultId = result.String("id");
            if (resultId is null || !splitsByResultId.TryGetValue(resultId, out var resultSplits))
            {
                result["splits"] = new JsonArray();
                continue;
            }

            result["splits"] = new JsonArray(resultSplits
                .Select(split => ToSplitJson(
                    split,
                    splitReferences,
                    leaderSplitTimes,
                    splitPositions))
                .ToArray());
        }
    }

    internal static void DecorateGapMs(IReadOnlyList<JsonObject> results)
    {
        var bestTime = results
            .Where(IsCompletedResult)
            .Select(result => result.Int("race_time_ms"))
            .Where(time => time is > 0)
            .DefaultIfEmpty()
            .Min();
        foreach (var result in results)
        {
            var raceTime = result.Int("race_time_ms");
            result["gap_ms"] = bestTime is > 0 && raceTime is > 0 && IsCompletedResult(result)
                ? raceTime.Value - bestTime.Value
                : null;
        }
    }

    internal static void DecorateLoadedTiming(
        IReadOnlyList<JsonObject> results,
        IReadOnlyList<JsonObject> splits,
        string timingMode)
    {
        var timingsByResult = BuildSectorsByResultId(
            splits,
            results,
            includeFinishSector: timingMode == TimingModeSectors);
        var allTimings = timingsByResult.Values.SelectMany(value => value).ToArray();
        var eligibleIds = results
            .Where(IsEligiblePerfectRunResult)
            .Select(result => result.String("id"))
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var eligibleTimings = timingsByResult
            .Where(pair => eligibleIds.Contains(pair.Key))
            .SelectMany(pair => pair.Value)
            .ToArray();
        var references = BuildTimingReferences(
            eligibleTimings,
            timing => timingMode == TimingModeSectors ? timing.SectorTimeMs : timing.RaceTimeMs);
        var leaderTimes = BuildLeaderTimes(
            results,
            allTimings,
            timing => timingMode == TimingModeSectors ? timing.SectorTimeMs : timing.RaceTimeMs);
        var statusOrders = results
            .Where(result => !string.IsNullOrWhiteSpace(result.String("id")))
            .ToDictionary(result => result.String("id")!, ResultStatusOrder, StringComparer.OrdinalIgnoreCase);
        var positions = timingMode == TimingModeSectors
            ? BuildSectorPositions(allTimings, statusOrders)
            : BuildSplitPositions(allTimings, statusOrders);

        foreach (var result in results)
        {
            if (!timingsByResult.TryGetValue(result.String("id") ?? string.Empty, out var timings))
            {
                result[timingMode] = new JsonArray();
                continue;
            }

            result[timingMode] = new JsonArray(timings.Select(timing =>
                timingMode == TimingModeSectors
                    ? ToSectorJson(timing, references, leaderTimes, positions)
                    : ToSplitJson(timing, references, leaderTimes, positions)).ToArray());
        }
    }

    internal static int ResultStatusOrder(JsonObject result)
    {
        return result.String("status")?.Trim().ToUpperInvariant() switch
        {
            "DNF" => 1,
            "DNS" => 2,
            "DSQ" => 3,
            _ when result.Int("position") is > 0 => 0,
            _ => 4
        };
    }
}
