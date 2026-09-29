using UCI_Stats_API.Features.Metrics;
using System.Text.Json.Nodes;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Models;

namespace UCI_Stats_API.Services;

public sealed class RankingService
{
    private static readonly TimeSpan SnapshotLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SnapshotComputationTimeout = TimeSpan.FromSeconds(90);

    private static readonly Dictionary<string, string> Sorts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["position"] = "rank",
        ["rank"] = "rank",
        ["fullName"] = "rider_name",
        ["rider_name"] = "rider_name",
        ["countryCode"] = "nation",
        ["nation"] = "nation",
        ["teamName"] = "team_name",
        ["team_name"] = "team_name",
        ["totalPoints"] = "points",
        ["points"] = "points",
        ["wins"] = "wins",
        ["podiums"] = "podiums",
        ["top_10"] = "top_10",
        ["top_20"] = "top_20",
        ["top_30"] = "top_30",
        ["starts"] = "starts",
        ["final_starts"] = "final_starts",
        ["averagePosition"] = "avg_position",
        ["avg_position"] = "avg_position",
        ["rating"] = "rating",
        ["pace"] = "pace",
        ["consistency"] = "consistency",
        ["confidence"] = "confidence",
        ["finalsCount"] = "finals_count",
        ["finals_count"] = "finals_count",
        ["finalsRate"] = "finals_count",
        ["finals_rate"] = "finals_count",
        ["qualifyingRate"] = "quali_rate",
        ["quali_rate"] = "quali_rate"
    };

    private readonly QueryValidationService _validator;
    private readonly DomainQueryService _domainQueries;
    private readonly MetricsService _metrics;
    private readonly RankingSingleFlight _singleFlight;
    private readonly IMemoryCache _cache;

    public RankingService(
        QueryValidationService validator,
        DomainQueryService domainQueries,
        MetricsService metrics,
        RankingSingleFlight singleFlight,
        IMemoryCache cache)
    {
        _validator = validator;
        _domainQueries = domainQueries;
        _metrics = metrics;
        _singleFlight = singleFlight;
        _cache = cache;
    }

    public async Task<RankingResponseDto> ListRankingAsync(IQueryCollection queryString, CancellationToken cancellationToken)
    {
        var page = _validator.Page(queryString);
        var requestedSort = _validator.Sort(queryString, Sorts, "points", "desc");
        var sort = new SortRequest(Sorts[requestedSort.Sort], requestedSort.Order);
        var startFilters = ReadStartFilters(queryString);
        var metricFilters = ReadMetricFilters(queryString);
        var context = await _domainQueries.ParseMetricsContextAsync(queryString, _validator, cancellationToken);
        var riderQuery = _validator.StringAny(queryString, "search", "q");
        var nation = _validator.String(queryString, "nation");
        var snapshot = await BuildSnapshotAsync(
            context,
            cancellationToken,
            nation: nation);
        var totalSessions = snapshot.Sessions
            .Select(session => session.String("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var totalFinalSessions = snapshot.Sessions
            .Where(DomainQueryService.IsFinalSession)
            .Select(session => session.String("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        JsonObject[] PrepareRows(RankingSnapshot source, bool applyCurrentRowFilters)
        {
            var sourceRows = source.Rows.AsEnumerable();
            if (applyCurrentRowFilters)
            {
                sourceRows = sourceRows
                    .Where(row => MetricFilters(row, metricFilters))
                    .Where(row => PointFilters(row, queryString))
                    .Where(row => StartFilters(row, startFilters));
            }

            return sourceRows
                .Select(row => row.DeepClone().AsObject())
                .ToArray();
        }

        var rows = PrepareRows(snapshot, applyCurrentRowFilters: true);

        AssignCanonicalPositions(rows, sort.Sort);
        var sorted = SortRows(rows, sort.Sort, sort.Descending);

        Dictionary<string, JsonObject>? previousRowsByRider = null;
        string? resolvedComparisonBasis = null;
        var requestedComparison = _validator.String(queryString, "comparison");
        JsonObject[]? comparisonRows = null;
        if (string.Equals(requestedComparison, "previousRound", StringComparison.OrdinalIgnoreCase) &&
            TryWithoutLatestRound(snapshot.Sessions, snapshot.FieldResults, out var previousSessions))
        {
            var previousSnapshot = await BuildSnapshotAsync(
                context,
                cancellationToken,
                resolvedSessions: previousSessions,
                nation: nation);
            comparisonRows = PrepareRows(previousSnapshot, applyCurrentRowFilters: true);
            resolvedComparisonBasis = "previousRound";
        }
        else if (string.Equals(requestedComparison, "defaultFilters", StringComparison.OrdinalIgnoreCase))
        {
            var defaultContext = DefaultRankingContext(context);
            var defaultSnapshot = await BuildSnapshotAsync(defaultContext, cancellationToken);
            var defaultRows = PrepareRows(defaultSnapshot, applyCurrentRowFilters: false);
            AssignCanonicalPositions(defaultRows, sort.Sort);

            if (SameSessionSet(snapshot.Sessions, defaultSnapshot.Sessions) &&
                SameRiderSet(rows, defaultRows) &&
                TryWithoutLatestRound(snapshot.Sessions, snapshot.FieldResults, out var defaultPreviousSessions))
            {
                var previousSnapshot = await BuildSnapshotAsync(
                    context,
                    cancellationToken,
                    resolvedSessions: defaultPreviousSessions,
                    nation: nation);
                comparisonRows = PrepareRows(previousSnapshot, applyCurrentRowFilters: true);
                resolvedComparisonBasis = "previousRound";
            }
            else
            {
                comparisonRows = defaultRows;
                resolvedComparisonBasis = "defaultFilters";
            }
        }
        else if (string.Equals(requestedComparison, "previousFilters", StringComparison.OrdinalIgnoreCase))
        {
            var previousFilterQuery = PreviousFilterQuery(queryString);
            var previousContext = await _domainQueries.ParseMetricsContextAsync(previousFilterQuery, _validator, cancellationToken);
            var previousSnapshot = await BuildSnapshotAsync(
                previousContext,
                cancellationToken);
            comparisonRows = PrepareRows(previousSnapshot, applyCurrentRowFilters: false);
            resolvedComparisonBasis = "previousFilters";
        }

        if (comparisonRows is not null)
        {
            AssignCanonicalPositions(comparisonRows, sort.Sort);
            previousRowsByRider = comparisonRows
                .Where(row => !string.IsNullOrWhiteSpace(row.String("rider_id")))
                .ToDictionary(row => row.String("rider_id")!, StringComparer.OrdinalIgnoreCase);
        }

        // Search changes visibility, not the ranking positions used for comparison.
        sorted = sorted.Where(row => DomainQueryService.MatchesRankingRider(
            row.Object("rider"), riderQuery, null)).ToArray();
        var data = sorted.Skip(page.Offset).Take(page.Limit).ToArray();
        var items = data.Select(row => ToPublicDto(
            row,
            previousRowsByRider?.GetValueOrDefault(row.String("rider_id") ?? string.Empty))).ToArray();
        var totalPages = sorted.Length == 0 ? 0 : (int)Math.Ceiling(sorted.Length / (double)page.Limit);
        return new RankingResponseDto(
            items,
            page.Page,
            page.Limit,
            sorted.Length,
            totalPages,
            page.Page > 1,
            page.Page < totalPages,
            new RankingSummaryDto(totalSessions, totalFinalSessions, sorted.Length),
            previousRowsByRider is null ? null : resolvedComparisonBasis);
    }

    internal Task<RankingSnapshot> BuildSnapshotAsync(
        RiderMetricsContext context,
        CancellationToken cancellationToken,
        IReadOnlyList<JsonObject>? resolvedSessions = null,
        string? riderQuery = null,
        string? nation = null,
        string? riderId = null)
    {
        var key = BuildSingleFlightKey(context, resolvedSessions, riderQuery, nation, riderId);
        if (_cache.TryGetValue<RankingSnapshot>(key, out var cached))
        {
            return Task.FromResult(cached!);
        }

        return _singleFlight.RunAsync(
            key,
            async () =>
            {
                if (_cache.TryGetValue<RankingSnapshot>(key, out var sharedCached))
                {
                    return sharedCached!;
                }

                // A shared calculation must not inherit the first waiter's disconnect token.
                // Each waiter can still cancel its own WaitAsync in RankingSingleFlight.
                using var computationTimeout = new CancellationTokenSource(SnapshotComputationTimeout);
                var snapshot = await BuildSnapshotCoreAsync(
                    context,
                    computationTimeout.Token,
                    resolvedSessions,
                    riderQuery,
                    nation,
                    riderId);
                _cache.Set(key, snapshot, SnapshotLifetime);
                return snapshot;
            },
            cancellationToken);
    }

    private async Task<RankingSnapshot> BuildSnapshotCoreAsync(
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
        var preaggregated = await _domainQueries.GetRankingPreaggregatesAsync(
            matchingSessions,
            riderQuery,
            nation,
            cancellationToken: cancellationToken);
        if (preaggregated is not null)
        {
            preaggregated = DomainQueryService.RestrictToCategories(preaggregated, context);
        }

        RankingDataBundle bundle;
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

    internal static string BuildSingleFlightKey(
        RiderMetricsContext context,
        IReadOnlyList<JsonObject>? resolvedSessions,
        string? riderQuery,
        string? nation,
        string? riderId = null)
    {
        return JsonSerializer.Serialize(new
        {
            context.SeasonYear,
            SeasonYears = context.SeasonYears.Distinct().Order().ToArray(),
            CategoryCode = Normalize(context.CategoryCode),
            CategoryCodes = Normalize(context.CategoryCodes),
            context.FromDate,
            context.ToDate,
            RoundId = Normalize(context.RoundId),
            TrackId = Normalize(context.TrackId),
            TrackIds = Normalize(context.TrackIds),
            CountryCode = Normalize(context.CountryCode),
            CountryCodes = Normalize(context.CountryCodes),
            context.ConditionId,
            ConditionIds = context.ConditionIds.Distinct().Order().ToArray(),
            ConditionNames = Normalize(context.ConditionNames),
            CompetitionClassCode = Normalize(context.CompetitionClassCode),
            CompetitionClassCodes = Normalize(context.CompetitionClassCodes),
            CompetitionClassIds = context.CompetitionClassIds.Distinct().Order().ToArray(),
            SessionType = Normalize(context.SessionType),
            SessionTypes = Normalize(context.SessionTypes),
            Status = Normalize(context.Status),
            Venue = Normalize(context.Venue),
            VenueNames = Normalize(context.VenueNames),
            TrackName = Normalize(context.TrackName),
            RiderQuery = Normalize(riderQuery),
            RiderId = Normalize(riderId),
            Nation = Normalize(nation),
            ResolvedSessionIds = Normalize(resolvedSessions?
                .Select(session => session.String("id") ?? string.Empty) ?? [])
        });
    }

    internal async Task<int?> GetPointsPositionAsync(
        RiderMetricsContext context,
        string riderId,
        CancellationToken cancellationToken)
    {
        var key = BuildSingleFlightKey(context, null, null, null);
        if (!_cache.TryGetValue<RankingSnapshot>(key, out var snapshot) || snapshot is null)
        {
            snapshot = await BuildSnapshotAsync(context, cancellationToken);
        }

        return SortRows(snapshot.Rows, "points", descending: true)
            .Select((row, index) => new { RiderId = row.String("rider_id"), Position = index + 1 })
            .FirstOrDefault(item => string.Equals(item.RiderId, riderId, StringComparison.OrdinalIgnoreCase))?
            .Position;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    private static string[] Normalize(IEnumerable<string> values) =>
        values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private JsonObject BuildRow(
        string riderId,
        IReadOnlyList<JsonObject> results,
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
        var teamHistory = resolvedTeam is null
            ? DomainQueryService.BuildTeamHistory(results)
            : new JsonArray(resolvedTeam.DeepClone());
        if (teamHistory.Count == 0 && fallbackTeamHistory is not null)
        {
            teamHistory = fallbackTeamHistory.DeepClone().AsArray();
        }

        if (rider is not null)
        {
            rider["teams"] = teamHistory;
        }
        var finalResults = results
            .Where(DomainQueryService.IsFinalResult)
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
            ["starts"] = results.Count,
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

    private static string? ResultSessionId(JsonObject result)
    {
        return result.String("session_id") ?? result.Object("sessions")?.String("id");
    }

    private static string? ResultRoundId(JsonObject result)
    {
        var session = result.Object("sessions");
        return session?.String("round_id") ??
            session?.Object("rounds")?.String("id") ??
            session?.Object("round_categories")?.String("round_id") ??
            session?.Object("round_categories")?.Object("rounds")?.String("id");
    }

    internal static bool HasStarted(JsonObject result)
    {
        return !string.Equals(
            result.String("status")?.Trim(),
            "DNS",
            StringComparison.OrdinalIgnoreCase);
    }

    internal static RankingParticipationRates CalculateParticipationRates(
        IReadOnlyList<JsonObject> results)
    {
        var startedResults = results.Where(HasStarted).ToArray();
        var qualifiedFinalResults = results
            .Where(DomainQueryService.IsFinalResult)
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

    internal MetricFilterValues ReadMetricFilters(IQueryCollection queryString)
    {
        var filters = new MetricFilterValues(
            _validator.DecimalAny(queryString, "minRating", "min_rating"),
            _validator.DecimalAny(queryString, "maxRating", "max_rating"),
            _validator.DecimalAny(queryString, "minPace", "min_pace"),
            _validator.DecimalAny(queryString, "maxPace", "max_pace"),
            _validator.DecimalAny(queryString, "minConsistency", "min_consistency"),
            _validator.DecimalAny(queryString, "maxConsistency", "max_consistency"),
            _validator.DecimalAny(queryString, "confidence", "minConfidence", "min_confidence"),
            _validator.DecimalAny(queryString, "maxConfidence", "max_confidence"));

        if (filters.MinConfidence is < 0m or > 100m ||
            filters.MaxConfidence is < 0m or > 100m)
        {
            throw _validator.BadQuery("confidence filters must be between 0 and 100.");
        }

        if (filters.MinConfidence > filters.MaxConfidence)
        {
            throw _validator.BadQuery("minConfidence cannot be greater than maxConfidence.");
        }

        return filters;
    }

    private static bool MetricFilters(JsonObject row, MetricFilterValues filters)
    {
        return WithinNullable(row.Decimal("rating"), filters.MinRating, filters.MaxRating) &&
            WithinNullable(row.Decimal("pace"), filters.MinPace, filters.MaxPace) &&
            WithinNullable(row.Decimal("consistency"), filters.MinConsistency, filters.MaxConsistency) &&
            WithinNullable(row.Decimal("confidence"), filters.MinConfidence, filters.MaxConfidence);
    }

    private bool PointFilters(JsonObject row, IQueryCollection queryString)
    {
        return Within(row.Decimal("points") ?? 0m, _validator.DecimalAny(queryString, "minPoints", "min_points"), _validator.DecimalAny(queryString, "maxPoints", "max_points"));
    }

    private StartFilterValues ReadStartFilters(IQueryCollection queryString)
    {
        var filters = new StartFilterValues(
            _validator.IntAny(queryString, "minStarts", "min_starts"),
            _validator.IntAny(queryString, "maxStarts", "max_starts"),
            _validator.IntAny(queryString, "minFinalStarts", "min_final_starts"),
            _validator.IntAny(queryString, "maxFinalStarts", "max_final_starts"));

        if (filters.Values().Any(value => value < 0))
        {
            throw _validator.BadQuery("Start filters must be greater than or equal to zero.");
        }

        if (filters.MinStarts > filters.MaxStarts)
        {
            throw _validator.BadQuery("min_starts cannot be greater than max_starts.");
        }

        if (filters.MinFinalStarts > filters.MaxFinalStarts)
        {
            throw _validator.BadQuery("min_final_starts cannot be greater than max_final_starts.");
        }

        return filters;
    }

    private static bool StartFilters(JsonObject row, StartFilterValues filters)
    {
        return Within(
                row.Int("starts") ?? 0,
                filters.MinStarts,
                filters.MaxStarts) &&
            Within(
                row.Int("final_starts") ?? 0,
                filters.MinFinalStarts,
                filters.MaxFinalStarts);
    }

    private static bool Within(decimal value, decimal? min, decimal? max)
    {
        return (min is null || value >= min.Value) && (max is null || value <= max.Value);
    }

    private sealed record StartFilterValues(
        int? MinStarts,
        int? MaxStarts,
        int? MinFinalStarts,
        int? MaxFinalStarts)
    {
        public IEnumerable<int> Values()
        {
            if (MinStarts.HasValue)
            {
                yield return MinStarts.Value;
            }

            if (MaxStarts.HasValue)
            {
                yield return MaxStarts.Value;
            }

            if (MinFinalStarts.HasValue)
            {
                yield return MinFinalStarts.Value;
            }

            if (MaxFinalStarts.HasValue)
            {
                yield return MaxFinalStarts.Value;
            }
        }
    }

    internal sealed record MetricFilterValues(
        decimal? MinRating,
        decimal? MaxRating,
        decimal? MinPace,
        decimal? MaxPace,
        decimal? MinConsistency,
        decimal? MaxConsistency,
        decimal? MinConfidence,
        decimal? MaxConfidence);

    private static bool WithinNullable(decimal? value, decimal? min, decimal? max)
    {
        if (min is null && max is null)
        {
            return true;
        }

        return value.HasValue && Within(value.Value, min, max);
    }

    internal static JsonObject[] SortRows(IReadOnlyList<JsonObject> rows, string sort, bool descending)
    {
        IOrderedEnumerable<JsonObject> ordered;
        if (sort is "rating" or "pace" or "consistency" or "confidence" or "finals_count" or "quali_rate" or "points" or "avg_position" or "wins" or "podiums" or "top_10" or "top_20" or "top_30" or "starts" or "final_starts")
        {
            ordered = descending
                ? rows.OrderByDescending(row => row.Decimal(sort) ?? decimal.MinValue)
                : rows.OrderBy(row => row.Decimal(sort) ?? decimal.MaxValue);
        }
        else if (sort == "rank")
        {
            ordered = descending
                ? rows.OrderByDescending(row => row.Int("rank") ?? int.MinValue)
                : rows.OrderBy(row => row.Int("rank") ?? int.MaxValue);
        }
        else
        {
            ordered = descending
                ? rows.OrderByDescending(row => row.String(sort) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                : rows.OrderBy(row => row.String(sort) ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        }

        return ordered
            .ThenBy(row => row.String("rider_name") ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => ParseLong(row.String("rider_id")))
            .ToArray();
    }

    internal static void AssignCanonicalPositions(IReadOnlyList<JsonObject> rows, string requestedSort)
    {
        var sort = requestedSort == "rank" ? "points" : requestedSort;
        var bestIsDescending = sort is not ("avg_position" or "rider_name" or "nation" or "team_name");
        var canonical = SortRows(rows, sort, bestIsDescending);
        for (var index = 0; index < canonical.Length; index++)
        {
            canonical[index]["rank"] = index + 1;
        }
    }

    private static RankingRiderDto ToPublicDto(
        JsonObject row,
        JsonObject? previousRow = null)
    {
        var rider = row.Object("rider") ?? new JsonObject();
        var team = rider.Array("teams")?.OfType<JsonObject>().FirstOrDefault();
        return new RankingRiderDto(
            row.Int("rank") ?? 0,
            ParseLong(row.String("rider_id")),
            rider.String("uci_id"),
            rider.String("first_name"),
            rider.String("last_name"),
            rider.DisplayName(),
            rider.String("nation"),
            NullableLong(team?.String("id")),
            team?.String("name") ?? row.String("team_name"),
            row.Decimal("points") ?? 0m,
            row.Decimal("rating"),
            row.Decimal("pace"),
            row.Decimal("consistency"),
            row.Decimal("confidence") ?? 0m,
            row.Int("finals_count") ?? 0,
            Math.Round((row.Decimal("quali_rate") ?? 0m) / 100m, 4, MidpointRounding.AwayFromZero),
            rider.String("profile_image_url"))
        {
            ConsistencyEvidence = row.Decimal("consistency_confidence") ?? 0m,
            PositionChange = previousRow is null
                ? null
                : (previousRow.Int("rank") ?? 0) - (row.Int("rank") ?? 0)
        };
    }

    internal static bool TryWithoutLatestRound(
        IReadOnlyList<JsonObject> sessions,
        IReadOnlyList<JsonObject> results,
        out IReadOnlyList<JsonObject> previousSessions)
    {
        var resultSessionIds = results
            .Select(ResultSessionId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resultRoundIds = results
            .Select(ResultRoundId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sessionRounds = sessions
            .Where(session =>
                resultSessionIds.Contains(session.String("id") ?? string.Empty) ||
                resultRoundIds.Contains(SessionRoundId(session) ?? string.Empty))
            .Select(session => new
            {
                RoundId = SessionRoundId(session),
                Date = SessionRoundDate(session)
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.RoundId) && item.Date is not null)
            .ToArray();
        var latest = sessionRounds
            .OrderByDescending(item => item.Date)
            .ThenByDescending(item => NumericId(item.RoundId))
            .ThenByDescending(item => item.RoundId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (latest is null)
        {
            previousSessions = [];
            return false;
        }

        var previousRoundIds = sessionRounds
            .Where(item => !string.Equals(
                item.RoundId,
                latest.RoundId,
                StringComparison.OrdinalIgnoreCase))
            .Select(item => item.RoundId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        previousSessions = sessions
            .Where(session => previousRoundIds.Contains(
                SessionRoundId(session) ?? string.Empty))
            .ToArray();
        return previousRoundIds.Count > 0 && previousSessions.Count > 0;
    }

    internal static QueryCollection PreviousFilterQuery(IQueryCollection query)
    {
        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);
        CopyComparisonValue(query, values, "comparisonSeason", "season");
        CopyComparisonValue(query, values, "comparisonTrackId", "trackId");
        CopyComparisonValue(query, values, "comparisonConditionId", "conditionId");
        CopyComparisonValue(query, values, "comparisonCompetitionClassId", "competitionClassId");
        CopyComparisonValue(query, values, "comparisonCategory", "category");
        return new QueryCollection(values);
    }

    private static void CopyComparisonValue(
        IQueryCollection source,
        IDictionary<string, StringValues> destination,
        string sourceKey,
        string destinationKey)
    {
        if (source.TryGetValue(sourceKey, out var value) && !StringValues.IsNullOrEmpty(value))
        {
            destination[destinationKey] = value;
        }
    }

    private static long NumericId(string? value) =>
        long.TryParse(value, out var parsed) ? parsed : long.MinValue;

    internal static RiderMetricsContext DefaultRankingContext(RiderMetricsContext context) =>
        new(
            context.SeasonYear,
            context.CategoryCode,
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
            SeasonYears = context.SeasonYears,
            CategoryCodes = context.CategoryCodes
        };

    internal static bool SameSessionSet(
        IReadOnlyList<JsonObject> left,
        IReadOnlyList<JsonObject> right) =>
        IdSet(left).SetEquals(IdSet(right));

    internal static bool SameRiderSet(
        IReadOnlyList<JsonObject> left,
        IReadOnlyList<JsonObject> right) =>
        RiderIdSet(left).SetEquals(RiderIdSet(right));

    private static HashSet<string> IdSet(IEnumerable<JsonObject> rows) =>
        rows
            .Select(row => row.String("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> RiderIdSet(IEnumerable<JsonObject> rows) =>
        rows
            .Select(row => row.String("rider_id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string? SessionRoundId(JsonObject session)
    {
        var round = session.Object("rounds") ??
            session.Object("round_categories")?.Object("rounds");
        return session.String("round_id") ?? round?.String("id");
    }

    private static DateOnly? SessionRoundDate(JsonObject session)
    {
        var round = session.Object("rounds") ??
            session.Object("round_categories")?.Object("rounds");
        return PublicDtoMapping.Date(
            round?.String("start_date") ?? session.String("session_date"));
    }

    private static long ParseLong(string? value) =>
        long.TryParse(value, out var parsed) ? parsed : 0;

    private static long? NullableLong(string? value) =>
        long.TryParse(value, out var parsed) ? parsed : null;
}

internal sealed record RankingSnapshot(
    IReadOnlyList<JsonObject> Rows,
    IReadOnlyList<JsonObject> Results,
    IReadOnlyList<JsonObject> FieldResults,
    IReadOnlyList<JsonObject> Splits,
    IReadOnlyList<JsonObject> Sessions,
    IReadOnlyList<PacePrecomputedSessionData>? PrecomputedSessions = null);

internal sealed record RankingParticipationRates(
    int FinalsCount,
    decimal QualifyingRate);
