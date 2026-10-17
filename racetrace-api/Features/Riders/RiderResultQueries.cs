using RaceTrace.Api.Features.Metrics;
using RaceTrace.Api.Features.Riders;
using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;

using RaceTrace.Api.Services;
using static RaceTrace.Api.Features.Riders.RiderStatisticsProjection;

namespace RaceTrace.Api.Features.Riders;

internal sealed class RiderResultQueries(
    QueryValidationService validator, DomainQueryService domainQueries,
    MetricsService metrics, RankingService ranking)
{
    private readonly QueryValidationService _validator = validator;
    private readonly DomainQueryService _domainQueries = domainQueries;
    private readonly MetricsService _metrics = metrics;
    private readonly RankingService _ranking = ranking;

    private static readonly Dictionary<string, string> RiderResultSorts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["round_number"] = "round_number",
        ["date"] = "date",
        ["session_name"] = "session_name",
        ["category"] = "category",
        ["position"] = "position",
        ["race_time_ms"] = "race_time_ms",
        ["gap_ms"] = "gap_ms",
        ["top_speed"] = "top_speed",
        ["completed_distance"] = "completed_distance",
        ["status"] = "status"
    };

    private static readonly Dictionary<string, string> VenueSorts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["name"] = "name",
        ["venue"] = "venue",
        ["country_code"] = "country_code",
        ["starts"] = "starts",
        ["wins"] = "wins",
        ["podiums"] = "podiums",
        ["top_10"] = "top_10",
        ["top_20"] = "top_20",
        ["top_30"] = "top_30",
        ["avg_position"] = "avg_position",
        ["best_position"] = "best_position",
        ["best_time_ms"] = "best_time_ms",
        ["rating"] = "rating",
        ["pace"] = "pace",
        ["consistency"] = "consistency"
    };

    private static readonly Dictionary<string, string> PublicRoundSorts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["roundStartDate"] = "roundStartDate",
        ["roundNumber"] = "roundNumber",
        ["season"] = "season",
        ["rating"] = "rating",
        ["pace"] = "pace",
        ["consistency"] = "consistency",
        ["confidence"] = "confidence",
        ["finalPosition"] = "finalPosition",
        ["awardedPoints"] = "awardedPoints"
    };

    private static readonly Dictionary<string, string> PublicTrackSorts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["trackName"] = "trackName",
        ["venueName"] = "venueName",
        ["countryCode"] = "countryCode",
        ["rating"] = "rating",
        ["pace"] = "pace",
        ["consistency"] = "consistency",
        ["confidence"] = "confidence",
        ["averageFinalPosition"] = "averageFinalPosition",
        ["startsCount"] = "startsCount"
    };

    public Task<PublicPagedResponse<RiderRoundResultDto>> ListPublicRiderRoundResultsAsync(
        string riderId,
        IQueryCollection queryString,
        CancellationToken cancellationToken) =>
        ListPublicRiderRoundResultsCoreAsync(riderId, queryString, cancellationToken, null);

    internal async Task<PublicPagedResponse<RiderRoundResultDto>> ListPublicRiderRoundResultsCoreAsync(
        string riderId,
        IQueryCollection queryString,
        CancellationToken cancellationToken,
        RankingSnapshot? suppliedSnapshot = null)
    {
        var page = _validator.Page(queryString);
        var sort = _validator.Sort(queryString, PublicRoundSorts, "roundStartDate", "desc");
        var context = await _domainQueries.ParseMetricsContextAsync(queryString, _validator, cancellationToken);
        var snapshot = suppliedSnapshot ?? await _ranking.BuildSnapshotAsync(
            context,
            cancellationToken,
            riderId: riderId);
        var results = snapshot.Results
            .Where(result => string.Equals(result.String("rider_id"), riderId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (results.Length == 0)
        {
            await EnsureRiderExistsAsync(riderId, cancellationToken);
        }

        var groups = results
            .GroupBy(ResultRoundId, StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .ToArray();
        var rows = new List<RiderRoundResultDto>(groups.Length);
        foreach (var group in groups)
        {
            var groupResults = group.ToArray();
            var groupPerformance = await CalculatePerformanceFromSnapshotAsync(
                riderId,
                groupResults,
                context,
                snapshot,
                cancellationToken);
            var performance = groupPerformance.GetValueOrDefault(riderId);
            rows.Add(ToRoundResult(group.Key, groupResults, performance));
        }

        var sorted = SortRoundResults(rows, sort.Sort, sort.Descending);
        var items = sorted.Skip(page.Offset).Take(page.Limit).ToArray();
        return PublicPagedResponse<RiderRoundResultDto>.Create(items, page.Page, page.Limit, sorted.Length);
    }

    public Task<PublicPagedResponse<RiderTrackStatisticsDto>> ListPublicTrackStatisticsAsync(
        string riderId,
        IQueryCollection queryString,
        CancellationToken cancellationToken) =>
        ListPublicTrackStatisticsCoreAsync(riderId, queryString, cancellationToken, null);

    internal async Task<PublicPagedResponse<RiderTrackStatisticsDto>> ListPublicTrackStatisticsCoreAsync(
        string riderId,
        IQueryCollection queryString,
        CancellationToken cancellationToken,
        RankingSnapshot? suppliedSnapshot = null)
    {
        var page = _validator.Page(queryString);
        var sort = _validator.Sort(queryString, PublicTrackSorts, "venueName", "asc");
        var context = await _domainQueries.ParseMetricsContextAsync(queryString, _validator, cancellationToken);
        var snapshot = suppliedSnapshot ?? await _ranking.BuildSnapshotAsync(
            context,
            cancellationToken,
            riderId: riderId);
        var results = snapshot.Results
            .Where(result => string.Equals(result.String("rider_id"), riderId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (results.Length == 0)
        {
            await EnsureRiderExistsAsync(riderId, cancellationToken);
        }

        var groups = results
            .GroupBy(result => result.Object("sessions")?.Object("rounds")?.Object("tracks")?.String("id") ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .ToArray();
        var rows = new List<RiderTrackStatisticsDto>(groups.Length);
        foreach (var group in groups)
        {
            var groupResults = group.ToArray();
            var groupPerformance = await CalculatePerformanceFromSnapshotAsync(
                riderId,
                groupResults,
                context,
                snapshot,
                cancellationToken);
            rows.Add(ToTrackStatistics(group.Key, groupResults, groupPerformance.GetValueOrDefault(riderId)));
        }

        var sorted = SortTrackResults(rows, sort.Sort, sort.Descending);
        var items = sorted.Skip(page.Offset).Take(page.Limit).ToArray();
        return PublicPagedResponse<RiderTrackStatisticsDto>.Create(items, page.Page, page.Limit, sorted.Length);
    }

    public async Task<ListResponse<JsonObject>> ListRiderResultsAsync(string riderId, IQueryCollection queryString, CancellationToken cancellationToken)
    {
        await EnsureRiderExistsAsync(riderId, cancellationToken);
        var page = _validator.Page(queryString);
        var sort = _validator.Sort(queryString, RiderResultSorts, "date", "desc");
        var context = await _domainQueries.ParseMetricsContextAsync(queryString, _validator, cancellationToken);
        var results = await _domainQueries.GetMatchingResultsAsync(context, riderId, riderQuery: _validator.String(queryString, "q"), cancellationToken: cancellationToken);
        await _domainQueries.DecorateGapMsAsync(results, cancellationToken: cancellationToken);
        var sorted = JsonSorting.Sort(DecorateResultRows(results), sort.Sort, sort.Descending);
        return ResponseFactory.Page(sorted.Skip(page.Offset).Take(page.Limit).ToArray(), page.Page, page.Limit, sorted.Count, sort.Sort, sort.Order);
    }

    public async Task<ListResponse<JsonObject>> ListVenuesAsync(string riderId, IQueryCollection queryString, CancellationToken cancellationToken)
    {
        await EnsureRiderExistsAsync(riderId, cancellationToken);
        var page = _validator.Page(queryString);
        var sort = _validator.Sort(queryString, VenueSorts, "venue", "asc");
        var context = await _domainQueries.ParseMetricsContextAsync(queryString, _validator, cancellationToken);
        var rows = await BuildVenueRowsAsync(riderId, context, cancellationToken);
        var q = _validator.String(queryString, "q");
        var countryCode = _validator.String(queryString, "country_code");
        var filtered = rows
            .Where(row =>
                string.IsNullOrWhiteSpace(q) ||
                (row.String("name") ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (row.String("venue") ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase))
            .Where(row => string.IsNullOrWhiteSpace(countryCode) || string.Equals(row.String("country_code"), countryCode, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var sorted = JsonSorting.Sort(filtered, sort.Sort, sort.Descending);
        return ResponseFactory.Page(sorted.Skip(page.Offset).Take(page.Limit).ToArray(), page.Page, page.Limit, sorted.Count, sort.Sort, sort.Order);
    }

    private async Task EnsureRiderExistsAsync(string riderId, CancellationToken cancellationToken)
    {
        if (await _domainQueries.FindByIdAsync("riders", riderId, "id", cancellationToken: cancellationToken) is null)
        {
            throw new ApiException(StatusCodes.Status404NotFound, "RIDER_NOT_FOUND", "Rider was not found.");
        }
    }

    internal async Task<IReadOnlyList<JsonObject>> BuildVenueRowsAsync(string riderId, RiderMetricsContext context, CancellationToken cancellationToken)
    {
        var results = await _domainQueries.GetMatchingResultsAsync(context, riderId, cancellationToken: cancellationToken);
        return results
            .GroupBy(result => result.Object("sessions")?.Object("rounds")?.Object("tracks")?.String("id") ?? string.Empty)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group =>
            {
                var rows = group.ToArray();
                var track = rows.First().Object("sessions")?.Object("rounds")?.Object("tracks");
                var finalPositions = rows
                    .Where(DomainQueryService.IsFinalResult)
                    .Select(result => result.Decimal("position"))
                    .Where(position => position is not null)
                    .Select(position => position!.Value)
                    .ToArray();
                var times = rows.Select(result => result.Decimal("race_time_ms")).Where(value => value is not null).Select(value => value!.Value).ToArray();

                return new JsonObject
                {
                    ["track_id"] = group.Key,
                    ["name"] = track?.String("name"),
                    ["venue"] = track?.String("venue"),
                    ["country_code"] = track?.String("country_code"),
                    ["starts"] = rows.Length,
                    ["wins"] = finalPositions.Count(value => value == 1),
                    ["podiums"] = finalPositions.Count(value => value <= 3),
                    ["top_10"] = finalPositions.Count(value => value <= 10),
                    ["top_20"] = finalPositions.Count(value => value <= 20),
                    ["top_30"] = finalPositions.Count(value => value <= 30),
                    ["avg_position"] = Average(finalPositions),
                    ["best_position"] = finalPositions.Length == 0 ? null : finalPositions.Min(),
                    ["best_time_ms"] = times.Length == 0 ? null : times.Min(),
                    ["rating"] = 0,
                    ["raw_rating"] = 0,
                    ["pace"] = 0,
                    ["raw_pace"] = 0,
                    ["consistency"] = 0,
                    ["result_score"] = 0
                };
            })
            .ToArray();
    }

    private static IReadOnlyList<JsonObject> DecorateResultRows(IReadOnlyList<JsonObject> results)
    {
        foreach (var result in results)
        {
            var session = result.Object("sessions");
            var round = session?.Object("rounds");
            result["date"] = session?.String("session_date") ?? round?.String("start_date");
            result["session_name"] = session?.String("name");
            result["round_number"] = round?.Int("round_number");
            result["category"] = session?.Object("categories")?.String("code");
        }

        return results;
    }

    private static decimal? Average(IEnumerable<decimal> values)
    {
        var numbers = values.ToArray();
        return numbers.Length == 0 ? null : numbers.Average();
    }

    private async Task<IReadOnlyDictionary<string, RiderPerformanceMetricValues>> CalculatePerformanceFromSnapshotAsync(
        string riderId,
        IReadOnlyList<JsonObject> riderResults,
        RiderMetricsContext context,
        RankingSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var sessionIds = riderResults
            .Select(result => result.String("session_id") ?? result.Object("sessions")?.String("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (snapshot.PrecomputedSessions is not null)
        {
            var precomputedSessions = snapshot.PrecomputedSessions
                .Where(session => sessionIds.Contains(session.SessionId))
                .ToArray();
            return _metrics.CalculatePrecomputedPerformanceByRider(
                riderResults,
                precomputedSessions,
                DistinctSessions(riderResults));
        }

        var fieldResults = snapshot.FieldResults
            .Where(result => sessionIds.Contains(result.String("session_id") ?? string.Empty))
            .ToArray();
        var fieldResultIds = fieldResults
            .Select(result => result.String("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var splits = snapshot.Splits
            .Where(split => fieldResultIds.Contains(split.String("result_id") ?? string.Empty))
            .ToArray();

        return await _metrics.CalculatePerformanceByRiderAsync(
            riderResults,
            context,
            cancellationToken,
            availableSessions: DistinctSessions(riderResults),
            suppliedSplits: splits,
            suppliedFieldResults: fieldResults);
    }

}
