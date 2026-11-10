using System.Text.Json.Nodes;
using RaceTrace.Api.Features.Metrics;
using RaceTrace.Api.Features.Results;
using RaceTrace.Api.Features.Riders;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Services;

public sealed class RidersService
{
    private static readonly Dictionary<string, string> RiderSorts = new[]
    {
        "id",
        "uci_id",
        "uci_code",
        "first_name",
        "last_name",
        "display_name",
        "nation",
        "birth_date",
        "age_at_event_year",
        "rating",
        "pace",
        "consistency",
        "points",
        "wins",
        "podiums",
        "top_10",
        "top_20",
        "top_30",
        "avg_position",
    }.ToDictionary(name => name, StringComparer.OrdinalIgnoreCase);

    private readonly ISupabaseRestClient _supabase;
    private readonly QueryValidationService _validator;
    private readonly DomainQueryService _domainQueries;
    private readonly MetricsService _metrics;
    private readonly RiderResultQueries _results;
    private readonly RiderOverviewQueries _overview;
    private readonly RiderHistoryQueries _history;

    public RidersService(
        ISupabaseRestClient supabase,
        QueryValidationService validator,
        DomainQueryService domainQueries,
        MetricsService metrics,
        RankingService ranking,
        AnalyticsService analytics
    )
    {
        _supabase = supabase;
        _validator = validator;
        _domainQueries = domainQueries;
        _metrics = metrics;
        _results = new RiderResultQueries(validator, domainQueries, metrics, ranking);
        _overview = new RiderOverviewQueries(
            validator,
            domainQueries,
            ranking,
            analytics,
            _results
        );
        _history = new RiderHistoryQueries(validator, domainQueries);
    }

    public async Task<ListResponse<JsonObject>> ListRidersAsync(
        IQueryCollection queryString,
        CancellationToken cancellationToken
    )
    {
        var page = _validator.Page(queryString);
        var sort = _validator.Sort(queryString, RiderSorts, "last_name", "asc");
        var context = await _domainQueries.ParseMetricsContextAsync(
            queryString,
            _validator,
            cancellationToken
        );
        var riderQuery = _validator.String(queryString, "q");
        var nation = _validator.String(queryString, "nation");
        var results = await _domainQueries.GetMatchingResultsAsync(
            context,
            riderQuery: riderQuery,
            nation: nation,
            cancellationToken: cancellationToken
        );
        var pointsByResultId = await _domainQueries.GetPointsByResultIdAsync(
            ResultIds(results),
            cancellationToken: cancellationToken
        );
        var performanceByRiderId = await _metrics.CalculatePerformanceByRiderAsync(
            results,
            context,
            cancellationToken,
            resultsContainFullField: string.IsNullOrWhiteSpace(riderQuery)
                && string.IsNullOrWhiteSpace(nation)
        );

        var rows = results
            .Where(result => result.Object("riders") is not null)
            .GroupBy(result => result.String("rider_id") ?? string.Empty)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group =>
                BuildRiderAggregate(
                    group.Key,
                    group.ToArray(),
                    pointsByResultId,
                    performanceByRiderId,
                    context
                )
            )
            .Where(row => MatchesMetricFilters(row, queryString))
            .ToArray();

        var sorted = SortRiderRows(rows, sort.Sort, sort.Descending);
        var data = sorted.Skip(page.Offset).Take(page.Limit).ToArray();
        return ResponseFactory.Page(
            data,
            page.Page,
            page.Limit,
            sorted.Count,
            sort.Sort,
            sort.Order
        );
    }

    public async Task<JsonObject> ResolveUciIdsAsync(
        string[] requestedUciIds,
        CancellationToken cancellationToken
    )
    {
        var uciIds = requestedUciIds
            .Select(value => value.Trim())
            .Where(value => value.Length > 0 && value.All(char.IsDigit))
            .Distinct(StringComparer.Ordinal)
            .Take(200)
            .ToArray();
        if (uciIds.Length == 0)
        {
            return new JsonObject { ["items"] = new JsonArray() };
        }

        var response = await _supabase.GetAsync(
            new SupabaseQuery("riders")
                .Select("id,uci_id")
                .In("uci_id", uciIds)
                .Limit(uciIds.Length),
            cancellationToken: cancellationToken
        );
        return new JsonObject
        {
            ["items"] = new JsonArray(
                response
                    .Data.Select(row => new JsonObject
                    {
                        ["riderId"] = row["id"]?.DeepClone(),
                        ["uciId"] = row["uci_id"]?.ToString(),
                    })
                    .ToArray()
            ),
        };
    }

    public async Task<JsonObject> GetRiderAsync(
        string riderId,
        string[] include,
        IQueryCollection queryString,
        CancellationToken cancellationToken
    )
    {
        var rider = await _domainQueries.FindByIdAsync(
            "riders",
            riderId,
            cancellationToken: cancellationToken
        );
        if (rider is null)
        {
            throw new ApiException(
                StatusCodes.Status404NotFound,
                "RIDER_NOT_FOUND",
                "Rider was not found."
            );
        }

        var context = await _domainQueries.ParseMetricsContextAsync(
            queryString,
            _validator,
            cancellationToken
        );
        var riderResults = await _domainQueries.GetMatchingResultsAsync(
            context,
            riderId,
            cancellationToken: cancellationToken
        );
        var teamHistory = await _domainQueries.GetRiderTeamHistoryAsync(
            [riderId],
            cancellationToken: cancellationToken
        );
        rider["teams"] = RiderTeamHistoryMapper.ToTeamHistoryJson(teamHistory);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        rider["current_category"] = RiderCategoryResolver.CurrentCategory(rider, today);
        rider["age"] = RiderCategoryResolver.CalculateAge(
            PublicDtoMapping.Date(rider.String("birth_date")),
            today
        );

        if (include.Contains("metrics", StringComparer.OrdinalIgnoreCase))
        {
            rider["metrics"] = JsonSerializerProxy.ToJsonValue(
                await _metrics.CalculateMetricsAsync(riderId, context, cancellationToken)
            );
        }

        if (include.Contains("results", StringComparer.OrdinalIgnoreCase))
        {
            await _domainQueries.DecorateMissingResultTeamsFromHistoryAsync(
                riderResults,
                cancellationToken
            );
            await _domainQueries.DecorateGapMsAsync(
                riderResults,
                cancellationToken: cancellationToken
            );
            rider["results"] = new JsonArray(riderResults.Select(row => row.DeepClone()).ToArray());
        }

        if (include.Contains("venues", StringComparer.OrdinalIgnoreCase))
        {
            rider["venues"] = new JsonArray(
                (await _results.BuildVenueRowsAsync(riderId, context, cancellationToken))
                    .Select(row => row.DeepClone())
                    .ToArray()
            );
        }

        return rider;
    }

    public async Task<JsonObject> GetMetricsAsync(
        string riderId,
        IQueryCollection queryString,
        CancellationToken cancellationToken
    )
    {
        await EnsureRiderExistsAsync(riderId, cancellationToken);
        var context = await _domainQueries.ParseMetricsContextAsync(
            queryString,
            _validator,
            cancellationToken
        );
        return JsonSerializerProxy.ToJsonObject(
            await _metrics.CalculateMetricsAsync(riderId, context, cancellationToken)
        );
    }

    public Task<IReadOnlyList<RiderTeamHistoryDto>> GetTeamHistoryAsync(
        string riderId,
        IQueryCollection queryString,
        CancellationToken cancellationToken
    ) => _history.GetTeamHistoryAsync(riderId, queryString, cancellationToken);

    public Task<RiderStatisticsDto> GetPublicStatisticsAsync(
        string riderId,
        IQueryCollection queryString,
        CancellationToken cancellationToken
    ) => _overview.GetPublicStatisticsAsync(riderId, queryString, cancellationToken);

    public Task<RiderOverviewDto> GetOverviewAsync(
        string riderId,
        IQueryCollection queryString,
        CancellationToken cancellationToken
    ) => _overview.GetOverviewAsync(riderId, queryString, cancellationToken);

    public Task<PublicPagedResponse<RiderRoundResultDto>> ListPublicRiderRoundResultsAsync(
        string riderId,
        IQueryCollection queryString,
        CancellationToken cancellationToken
    ) => _results.ListPublicRiderRoundResultsAsync(riderId, queryString, cancellationToken);

    public Task<PublicPagedResponse<RiderTrackStatisticsDto>> ListPublicTrackStatisticsAsync(
        string riderId,
        IQueryCollection queryString,
        CancellationToken cancellationToken
    ) => _results.ListPublicTrackStatisticsAsync(riderId, queryString, cancellationToken);

    public Task<ListResponse<JsonObject>> ListRiderResultsAsync(
        string riderId,
        IQueryCollection queryString,
        CancellationToken cancellationToken
    ) => _results.ListRiderResultsAsync(riderId, queryString, cancellationToken);

    public Task<ListResponse<JsonObject>> ListVenuesAsync(
        string riderId,
        IQueryCollection queryString,
        CancellationToken cancellationToken
    ) => _results.ListVenuesAsync(riderId, queryString, cancellationToken);

    internal static decimal? AveragePositionForType(
        IEnumerable<JsonObject> results,
        string publicSessionType
    ) => RiderStatisticsProjection.AveragePositionForType(results, publicSessionType);

    internal static RiderRoundResultDto ToRoundResult(
        string roundId,
        JsonObject[] results,
        RiderPerformanceMetricValues? performance
    ) => RiderStatisticsProjection.ToRoundResult(roundId, results, performance);

    private async Task EnsureRiderExistsAsync(string riderId, CancellationToken cancellationToken)
    {
        if (
            await _domainQueries.FindByIdAsync(
                "riders",
                riderId,
                "id",
                cancellationToken: cancellationToken
            )
            is null
        )
        {
            throw new ApiException(
                StatusCodes.Status404NotFound,
                "RIDER_NOT_FOUND",
                "Rider was not found."
            );
        }
    }

    private JsonObject BuildRiderAggregate(
        string riderId,
        JsonObject[] results,
        IReadOnlyDictionary<string, decimal> pointsByResultId,
        IReadOnlyDictionary<string, RiderPerformanceMetricValues> performanceByRiderId,
        RiderMetricsContext context
    )
    {
        var rider =
            results
                .Select(result => result.Object("riders"))
                .FirstOrDefault(row => row is not null)
                ?.DeepClone()
                ?.AsObject()
            ?? new JsonObject();
        var performance = performanceByRiderId.TryGetValue(riderId, out var riderPerformance)
            ? riderPerformance
            : new RiderPerformanceMetricValues(null, null, null, null, 0, 0, null, 0, 0, 0);
        rider["display_name"] = rider.DisplayName();
        rider["rating"] = performance.Rating;
        rider["raw_rating"] = performance.RawRating;
        rider["pace"] = performance.Pace;
        rider["raw_pace"] = performance.RawPace;
        rider["pace_confidence"] = performance.PaceConfidence;
        rider["pace_confidence_status"] = performance.PaceConfidenceStatus;
        rider["pace_used_sessions"] = performance.PaceUsedSessions;
        rider["pace_used_finals"] = performance.PaceUsedFinals;
        rider["pace_total_evidence"] = performance.PaceTotalEvidence;
        rider["pace_final_evidence"] = performance.PaceFinalEvidence;
        rider["consistency"] = performance.Consistency;
        rider["consistency_confidence"] = performance.ConsistencyConfidence;
        rider["consistency_evidence"] = performance.ConsistencyEvidence;
        rider["consistency_used_runs"] = performance.ConsistencyUsedRuns;
        rider["consistency_used_sectors"] = performance.ConsistencyUsedSectors;
        rider["result_score"] = performance.ResultScore;
        rider["result_score_confidence"] = performance.ResultScoreConfidence;
        rider["result_score_used_sessions"] = performance.ResultScoreUsedSessions;
        rider["points"] = SumPoints(results, pointsByResultId);
        var finalPositions = results
            .Where(ResultEmbedMapping.IsFinalResult)
            .Select(result => result.Decimal("position"))
            .Where(position => position is not null)
            .Select(position => position!.Value)
            .ToArray();
        rider["wins"] = finalPositions.Count(position => position == 1);
        rider["podiums"] = finalPositions.Count(position => position <= 3);
        rider["top_10"] = finalPositions.Count(position => position <= 10);
        rider["top_20"] = finalPositions.Count(position => position <= 20);
        rider["top_30"] = finalPositions.Count(position => position <= 30);
        rider["avg_position"] = Average(finalPositions);
        rider["starts"] = results.Length;
        rider["teams"] = RiderTeamHistoryMapper.BuildTeamHistory(results);
        rider["id"] = riderId;
        return rider;
    }

    private bool MatchesMetricFilters(JsonObject row, IQueryCollection queryString)
    {
        return NumberWithinNullable(
                row.Decimal("rating"),
                _validator.Decimal(queryString, "min_rating"),
                _validator.Decimal(queryString, "max_rating")
            )
            && NumberWithinNullable(
                row.Decimal("pace"),
                _validator.Decimal(queryString, "min_pace"),
                _validator.Decimal(queryString, "max_pace")
            )
            && NumberWithinNullable(
                row.Decimal("consistency"),
                _validator.Decimal(queryString, "min_consistency"),
                _validator.Decimal(queryString, "max_consistency")
            );
    }

    private static string[] ResultIds(IEnumerable<JsonObject> results)
    {
        return results
            .Select(result => result.String("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToArray();
    }

    private static decimal SumPoints(
        IEnumerable<JsonObject> results,
        IReadOnlyDictionary<string, decimal> pointsByResultId
    )
    {
        return results.Sum(result =>
            pointsByResultId.TryGetValue(result.String("id") ?? string.Empty, out var points)
                ? points
                : 0
        );
    }

    private static decimal? Average(IEnumerable<decimal> values)
    {
        var numbers = values.ToArray();
        return numbers.Length == 0 ? null : numbers.Average();
    }

    private static bool NumberWithin(decimal value, decimal? min, decimal? max)
    {
        return (min is null || value >= min.Value) && (max is null || value <= max.Value);
    }

    private static bool NumberWithinNullable(decimal? value, decimal? min, decimal? max)
    {
        if (min is null && max is null)
        {
            return true;
        }

        return value.HasValue && NumberWithin(value.Value, min, max);
    }

    private static IReadOnlyList<JsonObject> SortRiderRows(
        IReadOnlyList<JsonObject> rows,
        string sort,
        bool descending
    )
    {
        if (sort is not ("rating" or "pace" or "consistency"))
        {
            return JsonSorting.Sort(rows, sort, descending);
        }

        return descending
            ? rows.OrderByDescending(row => row.Decimal(sort) ?? decimal.MinValue).ToArray()
            : rows.OrderBy(row => row.Decimal(sort) ?? decimal.MaxValue).ToArray();
    }

    internal static IReadOnlyList<JsonObject> BuildSectorRows(
        IReadOnlyList<JsonObject> splits,
        IReadOnlyList<JsonObject>? results = null
    ) => RiderTimingStatistics.BuildSectorRows(splits, results);
}
