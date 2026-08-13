using System.Text.Json.Nodes;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Models;

namespace UCI_Stats_API.Services;

public sealed class PodiumPredictorService
{
    private const int ResultSize = 10;
    private const int ProbabilityFieldSize = 30;
    private const decimal MinimumCandidateConfidence = 35m;
    private const decimal CurrentSeasonRatingWeight = 0.40m;
    private const decimal TrackPaceWeight = 0.40m;
    private const decimal PreviousSeasonRatingWeight = 0.20m;
    private const decimal ConfidencePenaltyPerMissingPoint = 0.12m;
    private const double ProbabilityTemperature = 4.5;

    private readonly ISupabaseRestClient _supabase;
    private readonly DomainQueryService _domainQueries;
    private readonly MetricsService _metrics;
    private readonly SeasonSummaryService _seasonSummary;
    private readonly TimeProvider _timeProvider;

    public PodiumPredictorService(
        ISupabaseRestClient supabase,
        DomainQueryService domainQueries,
        MetricsService metrics,
        SeasonSummaryService seasonSummary,
        TimeProvider timeProvider)
    {
        _supabase = supabase;
        _domainQueries = domainQueries;
        _metrics = metrics;
        _seasonSummary = seasonSummary;
        _timeProvider = timeProvider;
    }

    public async Task<PodiumPredictionResponseDto> GetAsync(
        IQueryCollection queryString,
        CancellationToken cancellationToken)
    {
        var season = _seasonSummary.ReadSeason(queryString);
        var (publicCategory, categoryCode) = _seasonSummary.ReadCategory(queryString);
        var nextRound = await LoadNextRoundAsync(season, cancellationToken);
        if (nextRound is null)
        {
            return new PodiumPredictionResponseDto(
                season,
                publicCategory,
                null,
                []);
        }

        var trackId = nextRound.String("track_id") ??
            nextRound.Object("tracks")?.String("id");
        var nextRoundDate = PublicDtoMapping.Date(nextRound.String("start_date"));
        if (string.IsNullOrWhiteSpace(trackId) || nextRoundDate is null)
        {
            return new PodiumPredictionResponseDto(
                season,
                publicCategory,
                SeasonSummaryService.ToRoundDto(nextRound, _timeProvider.GetUtcNow()),
                []);
        }

        var currentTask = LoadMetricsAsync(
            SeasonContext(season, categoryCode),
            cancellationToken);
        var previousTask = LoadMetricsAsync(
            SeasonContext(season - 1, categoryCode),
            cancellationToken);
        var trackTask = LoadRecentTrackMetricsAsync(
            season,
            categoryCode,
            trackId,
            nextRoundDate.Value,
            cancellationToken);
        await Task.WhenAll(currentTask, previousTask, trackTask);
        var current = await currentTask;
        var previous = await previousTask;
        var track = await trackTask;
        var candidates = BuildCandidates(current, previous, track)
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Confidence)
            .ThenBy(candidate => candidate.RiderId, StringComparer.OrdinalIgnoreCase)
            .Take(ProbabilityFieldSize)
            .ToArray();
        var probabilities = CalculatePodiumProbabilities(
            candidates.Select(candidate => (double)candidate.Score).ToArray());

        var items = candidates
            .Select((candidate, index) => new
            {
                Candidate = candidate,
                Probability = probabilities[index]
            })
            .OrderByDescending(value => value.Probability)
            .ThenByDescending(value => value.Candidate.Score)
            .Take(ResultSize)
            .Select((value, index) =>
            {
                var identity = FindLatestIdentity(
                    value.Candidate.RiderId,
                    current.Results,
                    track.Results,
                    previous.Results);
                var rider = identity?.Object("riders") ?? new JsonObject();
                var team = identity?.Object("teams");
                return new PodiumPredictionRiderDto(
                    index + 1,
                    PublicDtoMapping.Long(value.Candidate.RiderId),
                    rider.DisplayName(),
                    rider.String("nation"),
                    PublicDtoMapping.NullableLong(team?.String("id")),
                    team?.String("name"),
                    Math.Round(
                        (decimal)(100d * value.Probability),
                        2,
                        MidpointRounding.AwayFromZero),
                    Math.Round(
                        value.Candidate.Score,
                        2,
                        MidpointRounding.AwayFromZero),
                    value.Candidate.CurrentRating,
                    value.Candidate.TrackPace,
                    value.Candidate.PreviousRating,
                    Math.Round(
                        value.Candidate.Confidence,
                        2,
                        MidpointRounding.AwayFromZero));
            })
            .ToArray();

        return new PodiumPredictionResponseDto(
            season,
            publicCategory,
            SeasonSummaryService.ToRoundDto(nextRound, _timeProvider.GetUtcNow()),
            items);
    }

    internal static IReadOnlyList<double> CalculatePodiumProbabilities(
        IReadOnlyList<double> scores)
    {
        if (scores.Count == 0)
        {
            return [];
        }

        if (scores.Count <= 3)
        {
            return Enumerable.Repeat(1d, scores.Count).ToArray();
        }

        var maximum = scores.Max();
        var strengths = scores
            .Select(score => Math.Exp((score - maximum) / ProbabilityTemperature))
            .ToArray();
        var total = strengths.Sum();
        var probabilities = new double[strengths.Length];

        for (var rider = 0; rider < strengths.Length; rider++)
        {
            var riderStrength = strengths[rider];
            var probability = riderStrength / total;
            for (var first = 0; first < strengths.Length; first++)
            {
                if (first == rider)
                {
                    continue;
                }

                var afterFirst = total - strengths[first];
                probability +=
                    strengths[first] / total *
                    riderStrength / afterFirst;

                for (var second = 0; second < strengths.Length; second++)
                {
                    if (second == rider || second == first)
                    {
                        continue;
                    }

                    var afterSecond = afterFirst - strengths[second];
                    probability +=
                        strengths[first] / total *
                        strengths[second] / afterFirst *
                        riderStrength / afterSecond;
                }
            }

            probabilities[rider] = Math.Clamp(probability, 0d, 1d);
        }

        return probabilities;
    }

    internal static decimal WeightedPredictionScore(
        decimal? currentRating,
        decimal? trackPace,
        decimal? previousRating)
    {
        decimal weighted = 0m;
        decimal weights = 0m;
        Add(currentRating, CurrentSeasonRatingWeight);
        Add(trackPace, TrackPaceWeight);
        Add(previousRating, PreviousSeasonRatingWeight);
        return weights == 0m ? 0m : weighted / weights;

        void Add(decimal? value, decimal weight)
        {
            if (value is null)
            {
                return;
            }

            weighted += value.Value * weight;
            weights += weight;
        }
    }

    internal static decimal ConfidenceAdjustedScore(
        decimal pace,
        decimal confidence)
    {
        return Math.Clamp(
            pace - ((100m - Math.Clamp(confidence, 0m, 100m)) *
                ConfidencePenaltyPerMissingPoint),
            0m,
            100m);
    }

    internal static IReadOnlyList<int> RecentTrackSeasons(int season)
    {
        var firstSeason = Math.Max(1990, season - 2);
        return Enumerable.Range(
            firstSeason,
            season - firstSeason + 1).ToArray();
    }

    private async Task<JsonObject?> LoadNextRoundAsync(
        int season,
        CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
        var response = await _supabase.GetAsync(
            new SupabaseQuery("rounds")
                .Select(
                    "id,context_name,season_year,round_number,track_id,start_date,end_date,competition_level_id," +
                    "tracks(id,name,venue,country_code,length_m,avg_slope)," +
                    "competition_levels(id,code,name,priority)")
                .Filter("season_year", "eq", season)
                .Filter("start_date", "gte", today)
                .Add("order", "start_date.asc.nullslast,id.asc")
                .Limit(1),
            cancellationToken: cancellationToken);
        var round = response.Data.FirstOrDefault();
        if (round is not null)
        {
            DomainQueryService.NormalizeRoundEmbed(round);
        }

        return round;
    }

    private async Task<MetricDataset> LoadMetricsAsync(
        RiderMetricsContext context,
        CancellationToken cancellationToken)
    {
        var sessions = await _domainQueries.GetMatchingSessionsAsync(
            context,
            cancellationToken: cancellationToken);
        var results = await _domainQueries.GetMatchingResultsAsync(
            context,
            cancellationToken: cancellationToken,
            resolvedSessions: sessions,
            select: DomainQueryService.RankingResultSelect);
        var metrics = await _metrics.CalculatePerformanceByRiderAsync(
            results,
            context,
            cancellationToken,
            resultsContainFullField: true,
            availableSessions: sessions);
        return new MetricDataset(results, metrics);
    }

    private async Task<MetricDataset> LoadRecentTrackMetricsAsync(
        int season,
        string categoryCode,
        string trackId,
        DateOnly nextRoundDate,
        CancellationToken cancellationToken)
    {
        var seasons = RecentTrackSeasons(season);
        var context = new RiderMetricsContext(
            null,
            categoryCode,
            null,
            nextRoundDate.AddDays(-1),
            null,
            trackId,
            null,
            null,
            null,
            null,
            null)
        {
            SeasonYears = seasons,
            TrackIds = [trackId],
            CategoryCodes = [categoryCode]
        };
        var sessions = await _domainQueries.GetMatchingSessionsAsync(
            context,
            cancellationToken: cancellationToken);
        var results = await _domainQueries.GetMatchingResultsAsync(
            context,
            cancellationToken: cancellationToken,
            resolvedSessions: sessions,
            select: DomainQueryService.RankingResultSelect);
        var latestRoundByRider = results
            .Where(result => !string.IsNullOrWhiteSpace(result.String("rider_id")))
            .GroupBy(
                result => result.String("rider_id")!,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                RiderId = group.Key,
                RoundId = group
                    .Select(result => new
                    {
                        RoundId = ResultRoundId(result),
                        Date = ResultDate(result)
                    })
                    .Where(value => !string.IsNullOrWhiteSpace(value.RoundId))
                    .OrderByDescending(value => value.Date)
                    .ThenByDescending(value => value.RoundId, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault()?
                    .RoundId
            })
            .Where(value => !string.IsNullOrWhiteSpace(value.RoundId))
            .ToDictionary(
                value => value.RiderId,
                value => value.RoundId!,
                StringComparer.OrdinalIgnoreCase);
        var selectedResults = results
            .Where(result =>
                latestRoundByRider.TryGetValue(
                    result.String("rider_id") ?? string.Empty,
                    out var roundId) &&
                string.Equals(
                    ResultRoundId(result),
                    roundId,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var metricTasks = selectedResults
            .GroupBy(
                result => ResultRoundId(result) ?? string.Empty,
                StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(async group =>
            {
                var groupResults = group.ToArray();
                var sessionIds = groupResults
                    .Select(ResultSessionId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var roundSessions = sessions
                    .Where(session => sessionIds.Contains(session.String("id") ?? string.Empty))
                    .ToArray();
                return await _metrics.CalculatePerformanceByRiderAsync(
                    groupResults,
                    context,
                    cancellationToken,
                    resultsContainFullField: false,
                    availableSessions: roundSessions);
            })
            .ToArray();
        var metricGroups = await Task.WhenAll(metricTasks);
        var metrics = metricGroups
            .SelectMany(group => group)
            .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().Value,
                StringComparer.OrdinalIgnoreCase);
        return new MetricDataset(selectedResults, metrics);
    }

    private static IReadOnlyList<PredictionCandidate> BuildCandidates(
        MetricDataset current,
        MetricDataset previous,
        MetricDataset track)
    {
        var riderIds = current.Metrics.Keys
            .Concat(previous.Metrics.Keys)
            .Concat(track.Metrics.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<PredictionCandidate>();
        foreach (var riderId in riderIds)
        {
            current.Metrics.TryGetValue(riderId, out var currentMetric);
            previous.Metrics.TryGetValue(riderId, out var previousMetric);
            track.Metrics.TryGetValue(riderId, out var trackMetric);
            var currentRating = currentMetric?.Rating;
            var previousRating = previousMetric?.Rating;
            var trackPace = trackMetric?.Pace;
            if (currentRating is null && previousRating is null && trackPace is null)
            {
                continue;
            }

            var confidence = currentMetric is not null
                ? currentMetric.PaceConfidence
                : Math.Max(
                    (previousMetric?.PaceConfidence ?? 0m) * 0.85m,
                    (trackMetric?.PaceConfidence ?? 0m) * 0.80m);
            if (confidence < MinimumCandidateConfidence)
            {
                continue;
            }

            var predictionScore = WeightedPredictionScore(
                currentRating,
                trackPace,
                previousRating);
            candidates.Add(new PredictionCandidate(
                riderId,
                currentRating,
                trackPace,
                previousRating,
                confidence,
                ConfidenceAdjustedScore(predictionScore, confidence)));
        }

        return candidates;
    }

    private static JsonObject? FindLatestIdentity(
        string riderId,
        params IReadOnlyList<JsonObject>[] resultSets)
    {
        return resultSets
            .SelectMany(results => results)
            .Where(result => string.Equals(
                result.String("rider_id"),
                riderId,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(ResultDate)
            .ThenByDescending(result => result.String("id"), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static RiderMetricsContext SeasonContext(
        int season,
        string categoryCode) =>
        new(
            season,
            categoryCode,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null)
        {
            SeasonYears = [season],
            CategoryCodes = [categoryCode]
        };

    private static string? ResultRoundId(JsonObject result)
    {
        var session = result.Object("sessions");
        return session?.String("round_id") ??
            session?.Object("rounds")?.String("id") ??
            session?.Object("round_categories")?.String("round_id") ??
            session?.Object("round_categories")?.Object("rounds")?.String("id");
    }

    private static string? ResultSessionId(JsonObject result) =>
        result.String("session_id") ?? result.Object("sessions")?.String("id");

    private static DateOnly? ResultDate(JsonObject result)
    {
        var session = result.Object("sessions");
        return PublicDtoMapping.Date(
            session?.String("session_date") ??
            session?.Object("rounds")?.String("start_date") ??
            session?.Object("round_categories")?.Object("rounds")?.String("start_date"));
    }

    private sealed record MetricDataset(
        IReadOnlyList<JsonObject> Results,
        IReadOnlyDictionary<string, RiderPerformanceMetricValues> Metrics);

    private sealed record PredictionCandidate(
        string RiderId,
        decimal? CurrentRating,
        decimal? TrackPace,
        decimal? PreviousRating,
        decimal Confidence,
        decimal Score);
}
