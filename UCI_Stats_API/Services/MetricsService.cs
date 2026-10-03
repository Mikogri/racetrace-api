using System.Text.Json.Nodes;
using UCI_Stats_API.Models;

namespace UCI_Stats_API.Services;

public sealed class MetricsService
{
    private readonly DomainQueryService _domainQueries;
    private readonly PaceService _pace;

    public MetricsService(DomainQueryService domainQueries, PaceService pace)
    {
        _domainQueries = domainQueries;
        _pace = pace;
    }

    public async Task<IReadOnlyDictionary<string, RiderPerformanceMetricValues>> CalculatePerformanceByRiderAsync(
        IReadOnlyList<JsonObject> results,
        RiderMetricsContext context,
        CancellationToken cancellationToken = default,
        bool resultsContainFullField = false,
        IReadOnlyList<JsonObject>? availableSessions = null,
        IReadOnlyList<JsonObject>? suppliedSplits = null,
        IReadOnlyList<JsonObject>? suppliedFieldResults = null)
    {
        availableSessions ??= await _domainQueries.GetMatchingSessionsAsync(
            context,
            cancellationToken: cancellationToken);
        return await _pace.CalculateAsync(
            results,
            context,
            cancellationToken,
            resultsContainFullField,
            availableSessions,
            suppliedSplits,
            suppliedFieldResults);
    }

    internal IReadOnlyDictionary<string, RiderPerformanceMetricValues> CalculatePrecomputedPerformanceByRider(
        RankingReadData data,
        IReadOnlyList<PacePrecomputedSessionData> precomputedSessions,
        IReadOnlyList<JsonObject>? availableSessions = null)
        => _pace.CalculatePrecomputed(data.Results, precomputedSessions, availableSessions);

    internal IReadOnlyDictionary<string, RiderPerformanceMetricValues> CalculatePrecomputedPerformanceByRider(
        IReadOnlyList<JsonObject> results,
        IReadOnlyList<PacePrecomputedSessionData> precomputedSessions,
        IReadOnlyList<JsonObject>? availableSessions = null)
    {
        return _pace.CalculatePrecomputed(
            results,
            precomputedSessions,
            availableSessions);
    }

    public async Task<RiderMetricValues> CalculateMetricsAsync(string riderId, RiderMetricsContext context, CancellationToken cancellationToken = default)
    {
        var results = await _domainQueries.GetMatchingResultsAsync(context, riderId, cancellationToken: cancellationToken);
        await _domainQueries.DecorateGapMsAsync(results, cancellationToken: cancellationToken);
        var resultIds = results.Select(result => result.String("id")).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!).ToArray();
        var pointsByResultId = await _domainQueries.GetPointsByResultIdAsync(resultIds, cancellationToken: cancellationToken);
        var performanceByRiderId = await CalculatePerformanceByRiderAsync(results, context, cancellationToken);
        var performance = performanceByRiderId.TryGetValue(riderId, out var riderPerformance)
            ? riderPerformance
            : new RiderPerformanceMetricValues(null, null, null, null, 0, 0, null, 0, 0, 0);

        var points = pointsByResultId.Values.Sum();
        var classified = results.Where(result => result.Bool("is_classified") == true || result.Int("position") is not null).ToArray();
        var finalClassified = classified.Where(DomainQueryService.IsFinalResult).ToArray();
        var finished = results.Count(result => IsFinished(result));
        var dnf = results.Count(result => string.Equals(result.String("status"), "DNF", StringComparison.OrdinalIgnoreCase));
        var dns = results.Count(result => string.Equals(result.String("status"), "DNS", StringComparison.OrdinalIgnoreCase));
        var dsq = results.Count(result => string.Equals(result.String("status"), "DSQ", StringComparison.OrdinalIgnoreCase));
        var finalPositions = finalClassified.Select(result => result.Decimal("position")).Where(value => value is not null).Select(value => value!.Value).ToArray();
        var gaps = classified.Select(result => result.Decimal("gap_ms")).Where(value => value is not null).Select(value => value!.Value).ToArray();
        var raceTimes = classified.Select(result => result.Decimal("race_time_ms")).Where(value => value is not null).Select(value => value!.Value).ToArray();
        var speeds = results.Select(result => result.Decimal("top_speed")).Where(value => value is not null).Select(value => value!.Value).ToArray();
        var bestResult = finalClassified
            .Where(result => result.Int("position") is not null)
            .OrderBy(result => result.Int("position"))
            .FirstOrDefault();

        return new RiderMetricValues(
            performance.Rating,
            performance.RawRating,
            performance.Pace,
            performance.RawPace,
            performance.PaceConfidence,
            performance.PaceUsedSessions,
            performance.Consistency,
            performance.ConsistencyConfidence,
            performance.ConsistencyUsedRuns,
            performance.ConsistencyUsedSectors,
            0m,
            points,
            null,
            finalPositions.Count(position => position == 1),
            finalPositions.Count(position => position <= 3),
            finalPositions.Count(position => position <= 10),
            finalPositions.Count(position => position <= 20),
            finalPositions.Count(position => position <= 30),
            results.Count,
            finished,
            dnf,
            dns,
            dsq,
            finalPositions.Length == 0 ? null : finalPositions.Average(),
            gaps.Length == 0 ? null : gaps.Average(),
            raceTimes.Length == 0 ? null : raceTimes.Average(),
            bestResult,
            speeds.Length == 0 ? null : speeds.Max())
        {
            PaceConfidenceStatus = performance.PaceConfidenceStatus,
            PaceUsedFinals = performance.PaceUsedFinals,
            PaceTotalEvidence = performance.PaceTotalEvidence,
            PaceFinalEvidence = performance.PaceFinalEvidence,
            ResultScore = performance.ResultScore,
            ResultScoreConfidence = performance.ResultScoreConfidence,
            ResultScoreUsedSessions = performance.ResultScoreUsedSessions
        };
    }

    public async Task AddMetricStubAsync(JsonObject row, string riderId, RiderMetricsContext context, CancellationToken cancellationToken = default)
    {
        var results = await _domainQueries.GetMatchingResultsAsync(context, riderId, cancellationToken: cancellationToken);
        var performanceByRiderId = await CalculatePerformanceByRiderAsync(results, context, cancellationToken);
        var performance = performanceByRiderId.TryGetValue(riderId, out var riderPerformance)
            ? riderPerformance
            : new RiderPerformanceMetricValues(null, null, null, null, 0, 0, null, 0, 0, 0);
        row["rating"] = performance.Rating;
        row["raw_rating"] = performance.RawRating;
        row["pace"] = performance.Pace;
        row["raw_pace"] = performance.RawPace;
        row["pace_confidence"] = performance.PaceConfidence;
        row["pace_confidence_status"] = performance.PaceConfidenceStatus;
        row["pace_used_sessions"] = performance.PaceUsedSessions;
        row["pace_used_finals"] = performance.PaceUsedFinals;
        row["pace_total_evidence"] = performance.PaceTotalEvidence;
        row["pace_final_evidence"] = performance.PaceFinalEvidence;
        row["consistency"] = performance.Consistency;
        row["consistency_confidence"] = performance.ConsistencyConfidence;
        row["consistency_used_runs"] = performance.ConsistencyUsedRuns;
        row["consistency_used_sectors"] = performance.ConsistencyUsedSectors;
        row["result_score"] = performance.ResultScore;
        row["result_score_confidence"] = performance.ResultScoreConfidence;
        row["result_score_used_sessions"] = performance.ResultScoreUsedSessions;
    }

    private static bool IsFinished(JsonObject result)
    {
        var status = result.String("status");
        return string.IsNullOrWhiteSpace(status) ||
            string.Equals(status, "FINISHED", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase) ||
            result.Bool("is_classified") == true;
    }
}
