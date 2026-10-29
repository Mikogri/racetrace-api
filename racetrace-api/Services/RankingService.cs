using RaceTrace.Api.Features.Metrics;
using RaceTrace.Api.Features.Ranking;
using static RaceTrace.Api.Features.Ranking.RankingResponseProjection;
using static RaceTrace.Api.Features.Ranking.RankingSnapshotBuilder;
using RaceTrace.Api.Infrastructure.Calculations;
using RaceTrace.Api.Infrastructure.Telemetry;
using System.Text.Json.Nodes;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Services;

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
    private readonly RankingSnapshotBuilder _snapshotBuilder;
    private readonly RankingSingleFlight _singleFlight;
    private readonly IMemoryCache _cache;
    private readonly DataRevisionService? _revisions;
    private readonly CalculationProvenance? _provenance;

    public RankingService(
        QueryValidationService validator,
        DomainQueryService domainQueries,
        MetricsService metrics,
        RankingSingleFlight singleFlight,
        IMemoryCache cache, DataRevisionService? revisions = null, CalculationProvenance? provenance = null)
    {
        _validator = validator;
        _domainQueries = domainQueries;
        _snapshotBuilder = new RankingSnapshotBuilder(domainQueries, metrics);
        _singleFlight = singleFlight;
        _cache = cache;
        _revisions = revisions;
        _provenance = provenance;
    }

    public async Task<RankingResponseDto> ListRankingAsync(IQueryCollection queryString, CancellationToken cancellationToken)
    {
        using var measurement = ApiTelemetry.Measure("ranking_total");
        var forceRefresh = queryString["refresh"] == "1";
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
            nation: nation,
            forceRefresh: forceRefresh);
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
            TryWithoutLatestRound(snapshot, out var previousSessions))
        {
            var previousSnapshot = await BuildSnapshotAsync(
                context,
                cancellationToken,
                resolvedSessions: previousSessions,
                nation: nation,
                forceRefresh: forceRefresh);
            comparisonRows = PrepareRows(previousSnapshot, applyCurrentRowFilters: true);
            resolvedComparisonBasis = "previousRound";
        }
        else if (string.Equals(requestedComparison, "defaultFilters", StringComparison.OrdinalIgnoreCase))
        {
            var defaultContext = DefaultRankingContext(context);
            var defaultSnapshot = await BuildSnapshotAsync(defaultContext, cancellationToken, forceRefresh: forceRefresh);
            var defaultRows = PrepareRows(defaultSnapshot, applyCurrentRowFilters: false);
            AssignCanonicalPositions(defaultRows, sort.Sort);

            if (SameSessionSet(snapshot.Sessions, defaultSnapshot.Sessions) &&
                SameRiderSet(rows, defaultRows) &&
                TryWithoutLatestRound(snapshot, out var defaultPreviousSessions))
            {
                var previousSnapshot = await BuildSnapshotAsync(
                    context,
                    cancellationToken,
                    resolvedSessions: defaultPreviousSessions,
                    nation: nation,
                    forceRefresh: forceRefresh);
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
                cancellationToken,
                forceRefresh: forceRefresh);
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
        using var projectionMeasurement = ApiTelemetry.Measure("ranking_projection");
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

    internal async Task<RankingSnapshot> BuildSnapshotAsync(
        RiderMetricsContext context,
        CancellationToken cancellationToken,
        IReadOnlyList<JsonObject>? resolvedSessions = null,
        string? riderQuery = null,
        string? nation = null,
        string? riderId = null,
        bool forceRefresh = false,
        Func<CancellationToken, Task<RiderDetailDataBundle>>? suppliedRiderBundleFactory = null)
    {
        var revision = _revisions is { Enabled: true } ? await _revisions.GetAsync(cancellationToken, forceRefresh) : null;
        var key = BuildSingleFlightKey(context, resolvedSessions, riderQuery, nation, riderId) +
            (revision is null ? "" : ":" + revision.ForContext(context) + ":" + AlgorithmCatalog.Current.Fingerprint("rating"));
        if (!forceRefresh && _cache.TryGetValue<RankingSnapshot>(key, out var cached))
        {
            ApiTelemetry.Cache("memory", "hit");
            _provenance?.Include(cached!.Metadata);
            return cached!;
        }

        ApiTelemetry.Cache("memory", "miss");
        var result = await _singleFlight.RunAsync(
            forceRefresh ? key + ":refresh" : key,
            async () =>
            {
                if (!forceRefresh && _cache.TryGetValue<RankingSnapshot>(key, out var sharedCached))
                {
                    return sharedCached!;
                }

                // A shared calculation must not inherit the first waiter's disconnect token.
                // Each waiter can still cancel its own WaitAsync in RankingSingleFlight.
                using var computationTimeout = new CancellationTokenSource(SnapshotComputationTimeout);
                var snapshot = await _snapshotBuilder.BuildAsync(
                    context,
                    computationTimeout.Token,
                    resolvedSessions,
                    riderQuery,
                    nation,
                    riderId,
                    suppliedRiderBundleFactory);
                if (revision is not null && revision.ForContext(context) !=
                    (await _revisions!.GetAsync(computationTimeout.Token, forceRefresh: true)).ForContext(context))
                    throw new ApiException(503, "DATA_REVISION_CHANGED", "Data changed during calculation. Please retry.");
                snapshot = snapshot with { Metadata = new CalculationMetadata(
                    AlgorithmCatalog.Current.VersionLabel("rating"), AlgorithmCatalog.Current.ConfigurationHash("rating"),
                    revision?.ForContext(context), DateTimeOffset.UtcNow) };
                _cache.Set(key, snapshot, SnapshotLifetime);
                return snapshot;
            },
            cancellationToken);
        _provenance?.Include(result.Metadata);
        return result;
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
        var revision = _revisions is { Enabled: true } ? await _revisions.GetAsync(cancellationToken) : null;
        var key = BuildSingleFlightKey(context, null, null, null) +
            (revision is null ? "" : ":" + revision.ForContext(context) + ":" + AlgorithmCatalog.Current.Fingerprint("rating"));
        if (!_cache.TryGetValue<RankingSnapshot>(key, out var snapshot) || snapshot is null)
        {
            var pointsKey = "points-position:" + key;
            if (!_cache.TryGetValue<JsonObject[]>(pointsKey, out var rows))
            {
                var sessions = await _domainQueries.GetMatchingSessionsAsync(context, cancellationToken: cancellationToken);
                rows = await _domainQueries.GetPointsSummaryAsync(context, sessions, cancellationToken);
                _cache.Set(pointsKey, rows, SnapshotLifetime);
            }
            return PointsPosition(rows!, riderId);
        }

        return PointsPosition(snapshot.Rows, riderId);
    }

    internal static int? PointsPosition(IReadOnlyList<JsonObject> rows, string riderId) =>
        SortRows(rows, "points", descending: true)
            .Select((row, index) => new { RiderId = row.String("rider_id"), Position = index + 1 })
            .FirstOrDefault(item => string.Equals(item.RiderId, riderId, StringComparison.OrdinalIgnoreCase))?
            .Position;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    private static string[] Normalize(IEnumerable<string> values) =>
        values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();


    internal static decimal Percentage(int numerator, int denominator) =>
        RankingSnapshotBuilder.Percentage(numerator, denominator);

    internal static bool HasStarted(JsonObject result) =>
        RankingSnapshotBuilder.HasStarted(result);

    internal static RankingParticipationRates CalculateParticipationRates(IReadOnlyList<JsonObject> results) =>
        RankingSnapshotBuilder.CalculateParticipationRates(results);

    internal static IReadOnlyDictionary<string, JsonArray> BuildFallbackTeamHistories(
        IReadOnlyList<JsonObject> selectedResults, IReadOnlyList<JsonObject> historicalTeamResults) =>
        RankingSnapshotBuilder.BuildFallbackTeamHistories(selectedResults, historicalTeamResults);

    internal static JsonObject[] SortRows(IReadOnlyList<JsonObject> rows, string sort, bool descending) =>
        RankingResponseProjection.SortRows(rows, sort, descending);

    internal static void AssignCanonicalPositions(IReadOnlyList<JsonObject> rows, string requestedSort) =>
        RankingResponseProjection.AssignCanonicalPositions(rows, requestedSort);

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

    internal static bool TryWithoutLatestRound(
        IReadOnlyList<JsonObject> sessions,
        IReadOnlyList<JsonObject> results,
        out IReadOnlyList<JsonObject> previousSessions)
        => TryWithoutLatestRound(sessions,results.Select(RankingReadResult.FromLegacy).ToArray(),out previousSessions);

    private static bool TryWithoutLatestRound(RankingSnapshot snapshot, out IReadOnlyList<JsonObject> previousSessions)
        => snapshot.ReadData is { } data
            ? TryWithoutLatestRound(snapshot.Sessions,data.FieldResults,out previousSessions)
            : TryWithoutLatestRound(snapshot.Sessions,snapshot.FieldResults,out previousSessions);

    private static bool TryWithoutLatestRound(
        IReadOnlyList<JsonObject> sessions,
        IReadOnlyList<RankingReadResult> results,
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
        Dictionary<string, StringValues> destination,
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

}

internal sealed record RankingSnapshot(
    IReadOnlyList<JsonObject> Rows,
    IReadOnlyList<JsonObject> Results,
    IReadOnlyList<JsonObject> FieldResults,
    IReadOnlyList<JsonObject> Splits,
    IReadOnlyList<JsonObject> Sessions,
    IReadOnlyList<PacePrecomputedSessionData>? PrecomputedSessions = null,
    RankingReadData? ReadData = null,
    CalculationMetadata? Metadata = null);

internal sealed record RankingParticipationRates(
    int FinalsCount,
    decimal QualifyingRate);
