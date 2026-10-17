using RaceTrace.Api.Features.Metrics;
using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Features.Ranking;

internal sealed class RankingSnapshotBuilder(DomainQueryService domainQueries, MetricsService metrics)
{
    private readonly DomainQueryService _domainQueries = domainQueries;
    private readonly MetricsService _metrics = metrics;

    internal async Task<RankingSnapshot> BuildAsync(
        RiderMetricsContext context,
        CancellationToken cancellationToken,
        IReadOnlyList<JsonObject>? resolvedSessions,
        string? riderQuery,
        string? nation,
        string? riderId)
    {
        if (!string.IsNullOrWhiteSpace(riderId))
        {
            var riderBundle = await _domainQueries.GetRiderDetailDataAsync(
                context,
                riderId,
                resolvedSessionIds: resolvedSessions?
                    .Select(session => session.String("id") ?? string.Empty),
                cancellationToken: cancellationToken);
            var riderData = riderBundle.Data;
            await _domainQueries.DecorateRankingRiderTeamsAsync(
                riderData.Results,
                cancellationToken);
            var riderPerformance = _metrics.CalculatePrecomputedPerformanceByRider(
                riderData.Results,
                riderBundle.PaceSessions,
                riderData.Sessions);
            var riderRows = riderData.Results
                .GroupBy(result => result.String("rider_id") ?? string.Empty)
                .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                .Select(group => BuildRow(
                    group.Key,
                    group.ToArray(),
                    riderData.PointsByResultId,
                    riderPerformance,
                    fallbackTeamHistory: null))
                .ToArray();

            return new RankingSnapshot(
                riderRows,
                riderData.Results,
                riderData.Results,
                riderData.Splits,
                riderData.Sessions,
                riderBundle.PaceSessions);
        }

        var matchingSessions = resolvedSessions ?? await _domainQueries.GetMatchingSessionsAsync(
            context,
            cancellationToken: cancellationToken);
        // Session packages contain all statuses. A status-filtered field must use
        // the corresponding source query, including its reference population.
        var preaggregated = string.IsNullOrWhiteSpace(context.Status) ? await _domainQueries.GetRankingPreaggregatesAsync(
            matchingSessions,
            riderQuery,
            nation,
            cancellationToken: cancellationToken,
            includeSplits: false) : null;
        if (preaggregated is not null)
        {
            preaggregated = DomainQueryService.RestrictToCategories(preaggregated, context);
        }

        RankingDataBundle bundle;
        if (preaggregated?.ReadData is { } read)
        {
            var performance = _metrics.CalculatePrecomputedPerformanceByRider(read,preaggregated.PaceSessions,matchingSessions);
            var typedRows = read.Results.GroupBy(r => r.String("rider_id") ?? string.Empty)
                .Where(g => !string.IsNullOrWhiteSpace(g.Key))
                .Select(g => BuildRow(g.Key,g.ToArray(),read.PointsByResultId,performance,null)).ToArray();
            var legacy = read.ToLegacy();
            return new RankingSnapshot(typedRows,legacy.Results,legacy.FieldResults,legacy.Splits,matchingSessions,preaggregated.PaceSessions,read);
        }
        IReadOnlyList<PacePrecomputedSessionData>? precomputedSessions;
        IReadOnlyDictionary<string, RiderPerformanceMetricValues> performanceByRiderId;
        if (preaggregated is not null)
        {
            bundle = preaggregated.Data;
            precomputedSessions = preaggregated.PaceSessions;
            performanceByRiderId = _metrics.CalculatePrecomputedPerformanceByRider(
                bundle.Results,
                precomputedSessions,
                matchingSessions);
        }
        else
        {
            bundle = await _domainQueries.GetRankingDataAsync(
                context,
                riderQuery: riderQuery,
                nation: nation,
                cancellationToken: cancellationToken,
                resolvedSessionIds: matchingSessions
                    .Select(session => session.String("id") ?? string.Empty));
            matchingSessions = bundle.Sessions;
            precomputedSessions = null;
            performanceByRiderId = await _metrics.CalculatePerformanceByRiderAsync(
                bundle.Results,
                context,
                cancellationToken,
                resultsContainFullField: true,
                availableSessions: matchingSessions,
                suppliedSplits: bundle.Splits,
                suppliedFieldResults: bundle.FieldResults);
        }
        var results = bundle.Results;
        var rows = results
            .GroupBy(result => result.String("rider_id") ?? string.Empty)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group => BuildRow(
                group.Key,
                group.ToArray(),
                bundle.PointsByResultId,
                performanceByRiderId,
                fallbackTeamHistory: null))
            .ToArray();
        return new RankingSnapshot(
            rows,
            results,
            bundle.FieldResults,
            bundle.Splits,
            matchingSessions,
            precomputedSessions);
    }

    private JsonObject BuildRow(
        string riderId,
        IReadOnlyList<JsonObject> results,
        IReadOnlyDictionary<string, decimal> pointsByResultId,
        IReadOnlyDictionary<string, RiderPerformanceMetricValues> performanceByRiderId,
        JsonArray? fallbackTeamHistory)
        => BuildRow(riderId,results.Select(RankingReadResult.FromLegacy).ToArray(),pointsByResultId,performanceByRiderId,fallbackTeamHistory);

    private JsonObject BuildRow(
        string riderId,
        RankingReadResult[] results,
        IReadOnlyDictionary<string, decimal> pointsByResultId,
        IReadOnlyDictionary<string, RiderPerformanceMetricValues> performanceByRiderId,
        JsonArray? fallbackTeamHistory)
    {
        var rider = results
            .Select(result => result.Object("riders"))
            .FirstOrDefault(row => row is not null)?
            .DeepClone()
            .AsObject();
        var resolvedTeam = rider?.Array("teams")?
            .OfType<JsonObject>()
            .FirstOrDefault();
        var explicitTeams = results.Where(result => !string.IsNullOrWhiteSpace(result.String("team_id"))).ToArray();
        var teamHistory = explicitTeams.Length > 0
            ? DomainQueryService.BuildTeamHistory(explicitTeams)
            : resolvedTeam is null
                ? DomainQueryService.BuildTeamHistory(results)
                : new JsonArray(resolvedTeam.DeepClone());
        if (teamHistory.Count == 0 && fallbackTeamHistory is not null)
        {
            teamHistory = fallbackTeamHistory.DeepClone().AsArray();
        }

        rider?["teams"] = teamHistory;
        var finalResults = results
            .Where(r => r.Session is not null && DomainQueryService.IsFinalSession(r.Session))
            .Where(HasStarted)
            .ToArray();
        var participationRates = CalculateParticipationRates(results);
        var finalPositions = finalResults.Select(result => result.Decimal("position")).Where(value => value is not null).Select(value => value!.Value).ToArray();
        var allPositions = results.Select(result => result.Decimal("position")).Where(value => value is not null).Select(value => value!.Value).ToArray();
        var performance = performanceByRiderId.TryGetValue(riderId, out var riderPerformance)
            ? riderPerformance
            : new RiderPerformanceMetricValues(null, null, null, null, 0, 0, null, 0, 0, 0);

        return new JsonObject
        {
            ["rider_id"] = riderId,
            ["rider"] = rider,
            ["rider_name"] = rider?.DisplayName(),
            ["nation"] = rider?.String("nation"),
            ["team_name"] = teamHistory
                .OfType<JsonObject>()
                .FirstOrDefault()?
                .String("name"),
            ["points"] = results.Sum(result => pointsByResultId.TryGetValue(result.String("id") ?? string.Empty, out var points) ? points : 0),
            ["wins"] = finalPositions.Count(value => value == 1),
            ["podiums"] = finalPositions.Count(value => value <= 3),
            ["top_10"] = finalPositions.Count(value => value <= 10),
            ["top_20"] = finalPositions.Count(value => value <= 20),
            ["top_30"] = finalPositions.Count(value => value <= 30),
            ["starts"] = results.Length,
            ["final_starts"] = finalResults.Length,
            ["finals_count"] = participationRates.FinalsCount,
            ["quali_rate"] = participationRates.QualifyingRate,
            ["avg_position"] = allPositions.Length == 0 ? null : allPositions.Average(),
            ["rating"] = performance.Rating,
            ["raw_rating"] = performance.RawRating,
            ["pace"] = performance.Pace,
            ["raw_pace"] = performance.RawPace,
            ["pace_confidence"] = performance.PaceConfidence,
            ["confidence"] = performance.PaceConfidence,
            ["pace_confidence_status"] = performance.PaceConfidenceStatus,
            ["pace_used_sessions"] = performance.PaceUsedSessions,
            ["pace_used_finals"] = performance.PaceUsedFinals,
            ["pace_total_evidence"] = performance.PaceTotalEvidence,
            ["pace_final_evidence"] = performance.PaceFinalEvidence,
            ["consistency"] = performance.Consistency,
            ["consistency_confidence"] = performance.ConsistencyConfidence,
            ["consistency_used_runs"] = performance.ConsistencyUsedRuns,
            ["consistency_used_sectors"] = performance.ConsistencyUsedSectors,
            ["result_score"] = performance.ResultScore,
            ["result_score_confidence"] = performance.ResultScoreConfidence,
            ["result_score_used_sessions"] = performance.ResultScoreUsedSessions
        };
    }

    internal static decimal Percentage(int numerator, int denominator)
    {
        if (denominator <= 0 || numerator <= 0)
        {
            return 0m;
        }

        return Math.Round(
            100m * numerator / denominator,
            2,
            MidpointRounding.AwayFromZero);
    }

    internal static string? ResultSessionId(JsonObject result)
        => ResultSessionId(RankingReadResult.FromLegacy(result));

    internal static string? ResultSessionId(RankingReadResult result)
    {
        return result.String("session_id") ?? result.Object("sessions")?.String("id");
    }

    internal static string? ResultRoundId(JsonObject result)
        => ResultRoundId(RankingReadResult.FromLegacy(result));

    internal static string? ResultRoundId(RankingReadResult result)
    {
        var session = result.Object("sessions");
        return session?.String("round_id") ??
            session?.Object("rounds")?.String("id") ??
            session?.Object("round_categories")?.String("round_id") ??
            session?.Object("round_categories")?.Object("rounds")?.String("id");
    }

    internal static bool HasStarted(JsonObject result)
        => HasStarted(RankingReadResult.FromLegacy(result));

    private static bool HasStarted(RankingReadResult result)
    {
        return !string.Equals(
            result.String("status")?.Trim(),
            "DNS",
            StringComparison.OrdinalIgnoreCase);
    }

    internal static RankingParticipationRates CalculateParticipationRates(
        IReadOnlyList<JsonObject> results)
        => CalculateParticipationRates(results.Select(RankingReadResult.FromLegacy).ToArray());

    private static RankingParticipationRates CalculateParticipationRates(
        IReadOnlyList<RankingReadResult> results)
    {
        var startedResults = results.Where(HasStarted).ToArray();
        var qualifiedFinalResults = results
            .Where(r => r.Session is not null && DomainQueryService.IsFinalSession(r.Session))
            .ToArray();
        var qualifiedFinalSessions = qualifiedFinalResults
            .Select(ResultSessionId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var participatedRoundIds = startedResults
            .Select(ResultRoundId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var qualifiedRoundIds = qualifiedFinalResults
            .Select(ResultRoundId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        participatedRoundIds.UnionWith(qualifiedRoundIds);

        return new RankingParticipationRates(
            qualifiedFinalSessions,
            Percentage(qualifiedRoundIds.Count, participatedRoundIds.Count));
    }

    internal static IReadOnlyDictionary<string, JsonArray> BuildFallbackTeamHistories(
        IReadOnlyList<JsonObject> selectedResults,
        IReadOnlyList<JsonObject> historicalTeamResults)
    {
        var targetDates = selectedResults
            .Where(result => !string.IsNullOrWhiteSpace(result.String("rider_id")))
            .GroupBy(result => result.String("rider_id")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(DomainQueryService.ResultDate)
                    .Where(date => !string.IsNullOrWhiteSpace(date))
                    .OrderByDescending(date => date, StringComparer.Ordinal)
                    .FirstOrDefault(),
                StringComparer.OrdinalIgnoreCase);

        return historicalTeamResults
            .Where(result => !string.IsNullOrWhiteSpace(result.String("rider_id")) &&
                result.Object("teams") is not null)
            .GroupBy(result => result.String("rider_id")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var rows = group.ToArray();
                    targetDates.TryGetValue(group.Key, out var targetDate);
                    var historicalRows = string.IsNullOrWhiteSpace(targetDate)
                        ? rows
                        : rows
                            .Where(result =>
                            {
                                var date = DomainQueryService.ResultDate(result);
                                return !string.IsNullOrWhiteSpace(date) &&
                                    string.CompareOrdinal(date, targetDate) <= 0;
                            })
                            .ToArray();

                    return DomainQueryService.BuildTeamHistory(
                        historicalRows.Length > 0 ? historicalRows : rows);
                },
                StringComparer.OrdinalIgnoreCase);
    }

}
