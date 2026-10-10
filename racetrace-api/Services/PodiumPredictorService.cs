using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Infrastructure.Calculations;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Services;

public sealed class PodiumPredictorService
{
    private const string PredictionRoundSelect =
        "id,context_name,season_year,round_number,track_id,start_date,end_date,competition_level_id," +
        "conditions(name,is_dry),tracks(id,name,venue,country_code,length_m,avg_slope)," +
        "competition_levels(id,code,name,priority)";

    private const string PredictionResultSelect =
        "id,rider_id,session_id,race_time_ms,top_speed,position,is_classified,status," +
        "riders(*),teams(*),result_points(points_awarded)," +
        "sessions(id,round_id,category_id,name,session_type,session_date,conditions(name,is_dry)," +
        "round_categories(round_id,category_id," +
        "rounds(id,track_id,competition_level_id,is_season_final_round,season_year,start_date," +
        "conditions(name,is_dry),competition_levels(id,code,name,priority)),categories(id,code)))";
    private const int ResultSize = 10;
    private const int ProbabilityFieldSize = 30;
    private const decimal MinimumCandidateConfidence = 35m;

    private readonly ISupabaseRestClient _supabase;
    private readonly DomainQueryService _domainQueries;
    private readonly MetricsService _metrics;
    private readonly SeasonSummaryService _seasonSummary;
    private readonly TimeProvider _timeProvider;
    private readonly CalculationProvenance? _provenance;
    private readonly PodiumPredictionOptions _options = AlgorithmConfiguration.Podium;

    public PodiumPredictorService(
        ISupabaseRestClient supabase,
        DomainQueryService domainQueries,
        MetricsService metrics,
        SeasonSummaryService seasonSummary,
        TimeProvider timeProvider, CalculationProvenance? provenance = null)
    {
        _supabase = supabase;
        _domainQueries = domainQueries;
        _metrics = metrics;
        _seasonSummary = seasonSummary;
        _timeProvider = timeProvider;
        _provenance = provenance;
    }

    public async Task<PodiumPredictionResponseDto> GetAsync(
        IQueryCollection queryString,
        CancellationToken cancellationToken)
    {
        var season = _seasonSummary.ReadSeason(queryString);
        var (publicCategory, categoryCode) = await _seasonSummary.ReadCategoryAsync(queryString, cancellationToken);
        if (IsJuniorCategory(categoryCode))
        {
            return new PodiumPredictionResponseDto(
                season,
                publicCategory,
                null,
                []);
        }

        var nextRound = await LoadTargetRoundAsync(
            season,
            queryString["roundId"].FirstOrDefault(),
            cancellationToken);
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

        var historyCutoff = nextRoundDate.Value.AddDays(-1);
        if (_provenance is not null)
        {
            _provenance.ModelVersion = _options.ModelVersion;
            _provenance.PredictionAsOf = _timeProvider.GetUtcNow();
            _provenance.HistoryCutoff = historyCutoff;
            _provenance.SimulationSeed = _options.RandomSeed;
            _provenance.SimulationIterations = _options.MonteCarloIterations;
        }
        var currentTask = LoadMetricsAsync(
            SeasonContext(season, categoryCode) with { ToDate = historyCutoff },
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
        var weekendTask = LoadWeekendMetricsAsync(
            nextRound.String("id")!,
            categoryCode,
            cancellationToken);
        await Task.WhenAll(currentTask, previousTask, trackTask, weekendTask);
        var current = await currentTask;
        var previous = await previousTask;
        var track = await trackTask;
        var weekend = await weekendTask;
        var historicalResults = current.Results.Concat(previous.Results).ToArray();
        var isWorldChampionship = IsWorldChampionship(nextRound);
        var targetCondition = TargetCondition(nextRound, weekend.Results);
        var roundsRemaining = await CountRemainingRoundsAsync(
            season,
            nextRoundDate.Value,
            cancellationToken);
        var featureBuilder = new PodiumPredictionFeatureBuilder(_options);
        var features = current.Metrics
            .Where(pair => pair.Value.PaceConfidence >= MinimumCandidateConfidence)
            .Select(pair =>
            {
                previous.Metrics.TryGetValue(pair.Key, out var previousMetric);
                track.Metrics.TryGetValue(pair.Key, out var trackMetric);
                var rider = FindLatestIdentity(pair.Key, current.Results)?.Object("riders");
                var wasJunior = RiderCategoryResolver.WasJuniorInSeason(
                    PublicDtoMapping.Date(rider?.String("birth_date")),
                    season - 1);
                return featureBuilder.Build(
                    pair.Key,
                    pair.Value,
                    wasJunior ? null : previousMetric,
                    wasJunior ? null : trackMetric,
                    historicalResults,
                    weekend.Results,
                    targetCondition,
                    isWorldChampionship,
                    roundsRemaining,
                    season - 1);
            })
            .OrderByDescending(feature => feature.Rating)
            .ThenByDescending(feature => feature.Confidence)
            .Take(ProbabilityFieldSize)
            .ToArray();
        var distributions = new PodiumPredictionCalibration(_options)
            .Calculate(features, isWorldChampionship);
        var simulations = new MonteCarloRaceSimulator(_options).Simulate(distributions);

        var items = distributions
            .Select((candidate, index) => new
            {
                Candidate = candidate,
                Simulation = simulations[index]
            })
            .OrderByDescending(value => value.Simulation.PodiumProbability)
            .ThenByDescending(value => value.Candidate.ExpectedPerformance)
            .Take(ResultSize)
            .Select((value, index) =>
            {
                var identity = FindLatestIdentity(
                    value.Candidate.Features.RiderId,
                    current.Results,
                    weekend.Results,
                    track.Results,
                    previous.Results);
                var rider = identity?.Object("riders") ?? new JsonObject();
                var team = identity?.Object("teams");
                return new PodiumPredictionRiderDto(
                    index + 1,
                    PublicDtoMapping.Long(value.Candidate.Features.RiderId),
                    rider.DisplayName(),
                    rider.String("nation"),
                    PublicDtoMapping.NullableLong(team?.String("id")),
                    team?.String("name"),
                    Percent(value.Simulation.PodiumProbability),
                    Rounded(value.Candidate.ExpectedPerformance),
                    value.Candidate.Features.CurrentRating,
                    value.Candidate.Features.TrackPace,
                    value.Candidate.Features.PreviousRating,
                    Rounded(value.Candidate.Features.Confidence))
                {
                    WinProbability = Percent(value.Simulation.WinProbability),
                    Top5Probability = Percent(value.Simulation.Top5Probability),
                    ExpectedPosition = Rounded(value.Simulation.ExpectedPosition),
                    ExpectedPerformance = Rounded(value.Candidate.ExpectedPerformance),
                    PerformanceUncertainty = Rounded(value.Candidate.PerformanceUncertainty),
                    IncidentProbability = Percent(value.Candidate.IncidentProbability),
                    Context = new PodiumPredictionContextDto(
                        Rounded(value.Candidate.Features.TrackAffinity),
                        Rounded(value.Candidate.Features.ConditionsAffinity),
                        Rounded(value.Candidate.Features.RecentForm),
                        Rounded(value.Candidate.Features.TitleLeverage),
                        Rounded(value.Candidate.Features.PressureResponse))
                };
            })
            .ToArray();

        return new PodiumPredictionResponseDto(
            season,
            publicCategory,
            SeasonSummaryService.ToRoundDto(nextRound, _timeProvider.GetUtcNow()),
            items)
        {
            ModelVersion = _options.ModelVersion
        };
    }

    private static decimal Rounded(double value) =>
        Math.Round((decimal)value, 2, MidpointRounding.AwayFromZero);

    private static decimal Percent(double probability) =>
        Rounded(100 * probability);

    internal static IReadOnlyList<int> RecentTrackSeasons(int season)
    {
        var firstSeason = Math.Max(1990, season - 2);
        return Enumerable.Range(
            firstSeason,
            season - firstSeason + 1).ToArray();
    }

    internal static bool IsJuniorCategory(string categoryCode) =>
        string.Equals(categoryCode, "JM", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(categoryCode, "JW", StringComparison.OrdinalIgnoreCase);

    private async Task<JsonObject?> LoadNextRoundAsync(
        int season,
        CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
        var response = await _supabase.GetAsync(
            new SupabaseQuery("rounds")
                .Select(PredictionRoundSelect)
                .Filter("season_year", "eq", season)
                .Filter("end_date", "gte", today)
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

    private async Task<JsonObject?> LoadTargetRoundAsync(
        int season,
        string? roundId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(roundId))
        {
            return await LoadNextRoundAsync(season, cancellationToken);
        }

        if (!long.TryParse(roundId, out var parsedRoundId) || parsedRoundId <= 0)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "INVALID_QUERY",
                "roundId must be a positive integer.");
        }

        var round = await _supabase.GetSingleAsync(
            new SupabaseQuery("rounds")
                .Select(PredictionRoundSelect)
                .Filter("id", "eq", parsedRoundId)
                .Filter("season_year", "eq", season),
            cancellationToken: cancellationToken);
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
            select: PredictionResultSelect);
        var metrics = await _metrics.CalculatePerformanceByRiderAsync(
            results,
            context,
            cancellationToken,
            resultsContainFullField: true,
            availableSessions: sessions);
        return new MetricDataset(results, metrics);
    }

    private async Task<MetricDataset> LoadWeekendMetricsAsync(
        string roundId,
        string categoryCode,
        CancellationToken cancellationToken)
    {
        var context = new RiderMetricsContext(
            null,
            categoryCode,
            null,
            null,
            roundId,
            null,
            null,
            null,
            null,
            null,
            null)
        {
            CategoryCodes = [categoryCode]
        };
        var sessions = await _domainQueries.GetMatchingSessionsAsync(
            context,
            cancellationToken: cancellationToken);
        var preFinalSessions = sessions
            .Where(session => PaceService.ParseSessionType(
                session.String("session_type"),
                session.String("name")) != PaceSessionType.Final)
            .ToArray();
        if (preFinalSessions.Length == 0)
        {
            return new MetricDataset([], new Dictionary<string, RiderPerformanceMetricValues>());
        }

        var results = await _domainQueries.GetMatchingResultsAsync(
            context,
            cancellationToken: cancellationToken,
            resolvedSessions: preFinalSessions,
            select: PredictionResultSelect);
        return new MetricDataset(results, new Dictionary<string, RiderPerformanceMetricValues>());
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
            select: PredictionResultSelect);
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

    private async Task<int> CountRemainingRoundsAsync(
        int season,
        DateOnly fromDate,
        CancellationToken cancellationToken)
    {
        var response = await _supabase.GetAsync(
            new SupabaseQuery("rounds")
                .Select("id")
                .Filter("season_year", "eq", season)
                .Filter("start_date", "gte", fromDate),
            cancellationToken: cancellationToken);
        return Math.Max(1, response.Data.Count);
    }

    private static bool IsWorldChampionship(JsonObject round)
    {
        var code = round.Object("competition_levels")?.String("code") ??
            round.Object("competition_class")?.String("code");
        return code is not null &&
            (code.Contains("champ", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(code, "WCH", StringComparison.OrdinalIgnoreCase));
    }

    private static string? TargetCondition(
        JsonObject round,
        IReadOnlyList<JsonObject> weekendResults)
    {
        var finalCondition = weekendResults
            .Select(result => result.Object("sessions"))
            .Where(session => session is not null &&
                PaceService.ParseSessionType(
                    session.String("session_type"),
                    session.String("name")) == PaceSessionType.Final)
            .Select(session => session!.Object("conditions")?.String("name"))
            .FirstOrDefault(code => !string.IsNullOrWhiteSpace(code));
        return finalCondition ??
            weekendResults.Select(result =>
                    result.Object("sessions")?.Object("conditions")?.String("name"))
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ??
            round.Object("conditions")?.String("name");
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

}
