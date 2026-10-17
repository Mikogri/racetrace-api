using System.Text.Json.Nodes;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Services;

internal sealed class PodiumPredictionFeatureBuilder
{
    private readonly PodiumPredictionOptions _options;

    public PodiumPredictionFeatureBuilder(PodiumPredictionOptions options)
    {
        _options = options;
    }

    public PodiumPredictionFeatures Build(
        string riderId,
        RiderPerformanceMetricValues currentMetric,
        RiderPerformanceMetricValues? previousMetric,
        RiderPerformanceMetricValues? trackMetric,
        IReadOnlyList<JsonObject> historicalResults,
        IReadOnlyList<JsonObject> weekendResults,
        string? targetCondition,
        bool isWorldChampionship,
        int roundsRemaining,
        int previousSeason)
    {
        var riderResults = historicalResults
            .Where(result => string.Equals(
                result.String("rider_id"), riderId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(ResultDate)
            .ThenBy(result => result.String("id"), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var observations = BuildObservations(riderResults, historicalResults);
        var normalPerformance = observations.Length == 0
            ? (double)(currentMetric.Pace ?? currentMetric.Rating ?? 50m)
            : observations.Average(value => value.Performance);
        var recentForm = RecentForm(observations, normalPerformance);
        var trackAffinity = Shrink(
            (double)((trackMetric?.Pace ?? currentMetric.Pace ?? 0m) -
                (currentMetric.Pace ?? 0m)),
            trackMetric?.PaceUsedSessions ?? 0,
            _options.AffinityShrinkageK);
        var conditionObservations = string.IsNullOrWhiteSpace(targetCondition)
            ? []
            : observations.Where(value => string.Equals(
                value.Condition,
                targetCondition,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        var conditionsAffinity = conditionObservations.Length == 0
            ? 0
            : Shrink(
                conditionObservations.Average(value => value.Performance) - normalPerformance,
                conditionObservations.Length,
                _options.AffinityShrinkageK);
        var weekendObservations = BuildObservations(
            weekendResults.Where(result => string.Equals(
                result.String("rider_id"), riderId, StringComparison.OrdinalIgnoreCase)).ToArray(),
            weekendResults);
        var weekendPerformance = weekendObservations.Length == 0
            ? 0
            : WeightedSessionPerformance(weekendObservations) - normalPerformance;
        var wchObservations = observations
            .Where(value => value.IsWorldChampionship)
            .ToArray();
        var wchDelta = !isWorldChampionship || wchObservations.Length == 0
            ? 0
            : Shrink(
                wchObservations.Average(value => value.Performance) - normalPerformance,
                wchObservations.Length,
                _options.WchShrinkageK);
        var titleLeverage = CalculateTitleLeverage(
            riderId,
            historicalResults,
            roundsRemaining);
        var pressureObservations = observations
            .Where(value => value.IsWorldChampionship || value.IsSeasonFinalRound)
            .ToArray();
        var pressureResponse = pressureObservations.Length == 0
            ? 0
            : Shrink(
                pressureObservations.Average(value => value.Performance) - normalPerformance,
                pressureObservations.Length,
                _options.PressureShrinkageK);
        var starts = riderResults.Count(result =>
            !string.Equals(result.String("status"), "DNS", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(result.String("status"), "DSQ", StringComparison.OrdinalIgnoreCase));
        var incidents = riderResults.Count(result =>
            string.Equals(result.String("status"), "DNF", StringComparison.OrdinalIgnoreCase));
        var incidentProbability =
            (incidents + _options.IncidentShrinkageK * _options.IncidentPriorProbability) /
            Math.Max(1, starts + _options.IncidentShrinkageK);

        return new PodiumPredictionFeatures(
            riderId,
            currentMetric.Rating,
            trackMetric?.Pace,
            previousMetric?.Rating,
            (double)(currentMetric.Rating ?? currentMetric.Pace ?? 50m),
            (double)(currentMetric.Pace ?? currentMetric.Rating ?? 50m),
            (double)currentMetric.PaceConfidence,
            (double)(currentMetric.Consistency ?? 50m),
            recentForm,
            trackAffinity,
            conditionsAffinity,
            weekendPerformance,
            wchDelta,
            titleLeverage,
            pressureResponse,
            incidentProbability,
            observations.Length + weekendObservations.Length);
    }

    private double RecentForm(
        IReadOnlyList<PerformanceObservation> observations,
        double normalPerformance)
    {
        if (observations.Count == 0)
        {
            return 0;
        }

        var weighted = 0.0;
        var weights = 0.0;
        var ordered = observations.OrderByDescending(value => value.Date).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var weight = Math.Pow(_options.RecentFormDecay, index);
            weighted += ordered[index].Performance * weight;
            weights += weight;
        }

        return weighted / weights - normalPerformance;
    }

    private static double WeightedSessionPerformance(
        IReadOnlyList<PerformanceObservation> observations)
    {
        var paceOptions = RaceTrace.Api.Infrastructure.Calculations.AlgorithmConfiguration.SessionWeights;
        var weighted = observations.Sum(value =>
            value.Performance * paceOptions.GetWeight(value.SessionType));
        var weights = observations.Sum(value => paceOptions.GetWeight(value.SessionType));
        return weights <= 0 ? observations.Average(value => value.Performance) : weighted / weights;
    }

    private static PerformanceObservation[] BuildObservations(
        IReadOnlyList<JsonObject> riderResults,
        IReadOnlyList<JsonObject> fieldResults)
    {
        var fieldSizeBySession = fieldResults
            .Where(IsClassified)
            .GroupBy(ResultSessionId, StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .ToDictionary(group => group.Key!, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        return riderResults
            .Where(IsClassified)
            .Select(result =>
            {
                var sessionId = ResultSessionId(result);
                var fieldSize = sessionId is null
                    ? 0
                    : fieldSizeBySession.GetValueOrDefault(sessionId);
                var rank = result.Int("position") ?? 0;
                var performance = fieldSize <= 1
                    ? 100
                    : 100.0 * (fieldSize - Math.Min(rank, fieldSize)) / (fieldSize - 1);
                var session = result.Object("sessions");
                var round = SessionRound(session);
                return new PerformanceObservation(
                    performance,
                    ResultDate(result),
                    PaceService.ParseSessionType(
                        session?.String("session_type"),
                        session?.String("name")),
                    session?.Object("conditions")?.String("name") ??
                        round?.Object("conditions")?.String("name"),
                    IsWch(round),
                    round?.Bool("is_season_final_round") == true);
            })
            .ToArray();
    }

    private double CalculateTitleLeverage(
        string riderId,
        IReadOnlyList<JsonObject> results,
        int roundsRemaining)
    {
        var pointsByRider = results
            .Where(result => !string.IsNullOrWhiteSpace(result.String("rider_id")))
            .GroupBy(result => result.String("rider_id")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(ResultPoints),
                StringComparer.OrdinalIgnoreCase);
        if (!pointsByRider.TryGetValue(riderId, out var points) || pointsByRider.Count == 0)
        {
            return 0;
        }

        var leader = pointsByRider.Values.Max();
        var maximumRemaining = Math.Max(1, roundsRemaining) *
            _options.MaximumPointsPerRound;
        var gap = Math.Max(0, leader - points);
        if (gap > maximumRemaining)
        {
            return 0;
        }

        var finalRoundBoost = roundsRemaining <= 1
            ? _options.FinalRoundTitleLeverageBoost
            : 0;
        return Math.Clamp(1.0 - gap / maximumRemaining + finalRoundBoost, 0, 1);
    }

    private static double ResultPoints(JsonObject result) =>
        result.Array("result_points")?.Sum(row =>
            row is JsonObject point ? (double)(point.Decimal("points_awarded") ?? 0m) : 0) ?? 0;

    private static double Shrink(double value, int count, double k) =>
        value * count / Math.Max(double.Epsilon, count + k);

    private static bool IsClassified(JsonObject result) =>
        result.Int("position") is > 0 &&
        (result.Bool("is_classified") == true || result.Int("position") is > 0) &&
        !string.Equals(result.String("status"), "DNS", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(result.String("status"), "DNF", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(result.String("status"), "DSQ", StringComparison.OrdinalIgnoreCase);

    private static string? ResultSessionId(JsonObject result) =>
        result.String("session_id") ?? result.Object("sessions")?.String("id");

    private static DateTimeOffset ResultDate(JsonObject result)
    {
        var session = result.Object("sessions");
        var value = session?.String("session_date") ?? SessionRound(session)?.String("start_date");
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.MinValue;
    }

    private static JsonObject? SessionRound(JsonObject? session) =>
        session?.Object("rounds") ?? session?.Object("round_categories")?.Object("rounds");

    private static bool IsWch(JsonObject? round)
    {
        var code = round?.Object("competition_levels")?.String("code") ??
            round?.Object("competition_class")?.String("code");
        return code is not null &&
            (code.Contains("champ", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(code, "WCH", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record PerformanceObservation(
        double Performance,
        DateTimeOffset Date,
        PaceSessionType SessionType,
        string? Condition,
        bool IsWorldChampionship,
        bool IsSeasonFinalRound);
}
