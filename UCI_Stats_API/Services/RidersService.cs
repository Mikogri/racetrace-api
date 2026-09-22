using UCI_Stats_API.Features.Metrics;
using UCI_Stats_API.Features.Riders;
using System.Text.Json.Nodes;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Models;

namespace UCI_Stats_API.Services;

public sealed class RidersService
{
    private static readonly string[] OverviewPresentationParameters =
    [
        "page",
        "pageSize",
        "page_size",
        "limit",
        "sort",
        "sortBy",
        "sort_by",
        "order",
        "sortDirection",
        "sort_direction"
    ];

    private static readonly Dictionary<string, string> RiderSorts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = "id",
        ["uci_id"] = "uci_id",
        ["uci_code"] = "uci_code",
        ["first_name"] = "first_name",
        ["last_name"] = "last_name",
        ["display_name"] = "display_name",
        ["nation"] = "nation",
        ["birth_date"] = "birth_date",
        ["age_at_event_year"] = "age_at_event_year",
        ["rating"] = "rating",
        ["pace"] = "pace",
        ["consistency"] = "consistency",
        ["points"] = "points",
        ["wins"] = "wins",
        ["podiums"] = "podiums",
        ["top_10"] = "top_10",
        ["top_20"] = "top_20",
        ["top_30"] = "top_30",
        ["avg_position"] = "avg_position"
    };

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

    private readonly ISupabaseRestClient _supabase;
    private readonly QueryValidationService _validator;
    private readonly DomainQueryService _domainQueries;
    private readonly MetricsService _metrics;
    private readonly RankingService _ranking;
    private readonly AnalyticsService _analytics;

    public RidersService(
        ISupabaseRestClient supabase,
        QueryValidationService validator,
        DomainQueryService domainQueries,
        MetricsService metrics,
        RankingService ranking,
        AnalyticsService analytics)
    {
        _supabase = supabase;
        _validator = validator;
        _domainQueries = domainQueries;
        _metrics = metrics;
        _ranking = ranking;
        _analytics = analytics;
    }

    public async Task<ListResponse<JsonObject>> ListRidersAsync(IQueryCollection queryString, CancellationToken cancellationToken)
    {
        var page = _validator.Page(queryString);
        var sort = _validator.Sort(queryString, RiderSorts, "last_name", "asc");
        var context = await _domainQueries.ParseMetricsContextAsync(queryString, _validator, cancellationToken);
        var riderQuery = _validator.String(queryString, "q");
        var nation = _validator.String(queryString, "nation");
        var results = await _domainQueries.GetMatchingResultsAsync(
            context,
            riderQuery: riderQuery,
            nation: nation,
            cancellationToken: cancellationToken);
        var pointsByResultId = await _domainQueries.GetPointsByResultIdAsync(ResultIds(results), cancellationToken: cancellationToken);
        var performanceByRiderId = await _metrics.CalculatePerformanceByRiderAsync(
            results,
            context,
            cancellationToken,
            resultsContainFullField: string.IsNullOrWhiteSpace(riderQuery) && string.IsNullOrWhiteSpace(nation));

        var rows = results
            .Where(result => result.Object("riders") is not null)
            .GroupBy(result => result.String("rider_id") ?? string.Empty)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group => BuildRiderAggregate(group.Key, group.ToArray(), pointsByResultId, performanceByRiderId, context))
            .Where(row => MatchesMetricFilters(row, queryString))
            .ToArray();

        var sorted = SortRiderRows(rows, sort.Sort, sort.Descending);
        var data = sorted.Skip(page.Offset).Take(page.Limit).ToArray();
        return ResponseFactory.Page(data, page.Page, page.Limit, sorted.Count, sort.Sort, sort.Order);
    }

    public async Task<JsonObject> ResolveUciIdsAsync(
        string[] requestedUciIds,
        CancellationToken cancellationToken)
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
            cancellationToken: cancellationToken);
        return new JsonObject
        {
            ["items"] = new JsonArray(response.Data.Select(row => new JsonObject
            {
                ["riderId"] = row["id"]?.DeepClone(),
                ["uciId"] = row["uci_id"]?.ToString()
            }).ToArray())
        };
    }

    public async Task<JsonObject> GetRiderAsync(string riderId, string[] include, IQueryCollection queryString, CancellationToken cancellationToken)
    {
        var rider = await _domainQueries.FindByIdAsync("riders", riderId, cancellationToken: cancellationToken);
        if (rider is null)
        {
            throw new ApiException(StatusCodes.Status404NotFound, "RIDER_NOT_FOUND", "Rider was not found.");
        }

        var context = await _domainQueries.ParseMetricsContextAsync(queryString, _validator, cancellationToken);
        var riderResults = await _domainQueries.GetMatchingResultsAsync(context, riderId, cancellationToken: cancellationToken);
        var teamHistory = await _domainQueries.GetRiderTeamHistoryAsync(
            [riderId],
            cancellationToken: cancellationToken);
        rider["teams"] = RiderTeamHistoryMapper.ToTeamHistoryJson(teamHistory);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        rider["current_category"] = RiderCategoryResolver.CurrentCategory(rider, today);
        rider["age"] = RiderCategoryResolver.CalculateAge(
            PublicDtoMapping.Date(rider.String("birth_date")),
            today);

        if (include.Contains("metrics", StringComparer.OrdinalIgnoreCase))
        {
            rider["metrics"] = JsonSerializerProxy.ToJsonObject(await _metrics.CalculateMetricsAsync(riderId, context, cancellationToken));
        }

        if (include.Contains("results", StringComparer.OrdinalIgnoreCase))
        {
            await _domainQueries.DecorateMissingResultTeamsFromHistoryAsync(
                riderResults,
                cancellationToken);
            await _domainQueries.DecorateGapMsAsync(riderResults, cancellationToken: cancellationToken);
            rider["results"] = new JsonArray(riderResults.Select(row => row.DeepClone()).ToArray());
        }

        if (include.Contains("venues", StringComparer.OrdinalIgnoreCase))
        {
            rider["venues"] = new JsonArray((await BuildVenueRowsAsync(riderId, context, cancellationToken)).Select(row => row.DeepClone()).ToArray());
        }

        return rider;
    }

    public async Task<IReadOnlyList<RiderTeamHistoryDto>> GetTeamHistoryAsync(
        string riderId,
        IQueryCollection queryString,
        CancellationToken cancellationToken)
    {
        await EnsureRiderExistsAsync(riderId, cancellationToken);
        var fromDate = _validator.DateAny(
            queryString,
            "dateFrom",
            "date_from",
            "from_date");
        var toDate = _validator.DateAny(
            queryString,
            "dateTo",
            "date_to",
            "to_date");
        _validator.ValidateDateRange(fromDate, toDate);
        var history = await _domainQueries.GetRiderTeamHistoryAsync(
            [riderId],
            fromDate,
            toDate,
            cancellationToken: cancellationToken);

        return history
            .OrderByDescending(row => row.String("valid_from"), StringComparer.Ordinal)
            .Select(row =>
            {
                var team = row.Object("teams");
                var validFrom = PublicDtoMapping.Date(row.String("valid_from")) ??
                    throw new InvalidOperationException("Team history has no valid_from date.");
                var validTo = PublicDtoMapping.Date(row.String("valid_to"));
                return new RiderTeamHistoryDto(
                    PublicDtoMapping.Long(row.String("id")),
                    PublicDtoMapping.Long(row.String("rider_id")),
                    PublicDtoMapping.Long(row.String("team_id")),
                    team?.String("name"),
                    team?.String("code"),
                    validFrom,
                    validTo,
                    validTo is null,
                    PublicDtoMapping.NullableLong(row.String("source_round_id")));
            })
            .ToArray();
    }

    public async Task<JsonObject> GetMetricsAsync(string riderId, IQueryCollection queryString, CancellationToken cancellationToken)
    {
        await EnsureRiderExistsAsync(riderId, cancellationToken);
        var context = await _domainQueries.ParseMetricsContextAsync(queryString, _validator, cancellationToken);
        return JsonSerializerProxy.ToJsonObject(await _metrics.CalculateMetricsAsync(riderId, context, cancellationToken));
    }

    public Task<RiderStatisticsDto> GetPublicStatisticsAsync(
        string riderId,
        IQueryCollection queryString,
        CancellationToken cancellationToken) =>
        GetPublicStatisticsCoreAsync(riderId, queryString, cancellationToken, null, null);

    private async Task<RiderStatisticsDto> GetPublicStatisticsCoreAsync(
        string riderId,
        IQueryCollection queryString,
        CancellationToken cancellationToken,
        RankingSnapshot? suppliedSnapshot = null,
        int? suppliedRankingPosition = null)
    {
        var context = await _domainQueries.ParseMetricsContextAsync(queryString, _validator, cancellationToken);
        var currentPresentationTask = GetCurrentPresentationAsync(
            riderId,
            cancellationToken);
        var snapshot = suppliedSnapshot ?? await _ranking.BuildSnapshotAsync(
            context,
            cancellationToken,
            riderId: riderId);
        var currentPresentation = await currentPresentationTask;
        var riderRow = snapshot.Rows.FirstOrDefault(row => string.Equals(
            row.String("rider_id"),
            riderId,
            StringComparison.OrdinalIgnoreCase));
        if (riderRow is null)
        {
            return EmptyPublicStatistics(
                riderId,
                currentPresentation);
        }

        var rider = currentPresentation.Rider;
        var riderResults = snapshot.Results
            .Where(result => string.Equals(result.String("rider_id"), riderId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var allResults = snapshot.FieldResults;
        var performance = PerformanceFromRankingRow(riderRow);
        var rankingPosition = suppliedRankingPosition ??
            await _ranking.GetPointsPositionAsync(context, riderId, cancellationToken);

        var targetResultIds = riderResults
            .Select(result => result.String("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var splits = snapshot.Splits;
        var sectorRows = BuildSectorRows(splits, allResults);
        var splitWins = RiderTimingStatistics.CountTimingWins(splits, allResults, targetResultIds, useSectorTimes: false);
        var sectorWins = RiderTimingStatistics.CountTimingWins(sectorRows, allResults, targetResultIds, useSectorTimes: true);
        var targetSectorRows = sectorRows
            .Where(row => targetResultIds.Contains(row.String("result_id") ?? string.Empty) && row.Int("sector_time_ms") is > 0)
            .ToArray();
        var averageSectorTimes = targetSectorRows
            .GroupBy(row => row.Int("split_index")!.Value)
            .OrderBy(group => group.Key)
            .Select(group => new AverageSectorTimeDto(
                group.Key,
                Math.Round(group.Average(row => (decimal)row.Int("sector_time_ms")!.Value) / 1000m, 3),
                group.Count()))
            .ToArray();

        var qualifying = riderResults.Where(result => PublicApiCodes.IsQualification(SessionType(result))).ToArray();
        var finals = riderResults.Where(result => PublicApiCodes.IsFinal(SessionType(result))).ToArray();
        var qualifyingPositions = qualifying.Select(ToSessionPosition).ToArray();
        var finalPositions = finals.Select(ToSessionPosition).ToArray();
        var classifiedFinalPositions = finals
            .Select(result => result.Int("position"))
            .Where(position => position is > 0)
            .Select(position => position!.Value)
            .ToArray();
        var latestResult = riderResults.OrderByDescending(ResultDate).FirstOrDefault();
        var currentCategory = currentPresentation.CurrentCategory ??
            PublicApiCodes.ToPublicCategory(
                latestResult?.Object("sessions")?.Object("categories")?.String("code"));
        var team = currentPresentation.Team;

        var targetSplits = splits.Where(split => targetResultIds.Contains(split.String("result_id") ?? string.Empty)).ToArray();
        var qualificationResultIds = qualifying.Select(result => result.String("id") ?? string.Empty).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var finalResultIds = finals.Select(result => result.String("id") ?? string.Empty).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var preFinalResults = riderResults.Where(result => !PublicApiCodes.IsFinal(SessionType(result))).ToArray();
        var preFinalResultIds = preFinalResults.Select(result => result.String("id") ?? string.Empty).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allPreFinalResults = allResults.Where(result => !PublicApiCodes.IsFinal(SessionType(result))).ToArray();
        var allFinalResults = allResults.Where(result => PublicApiCodes.IsFinal(SessionType(result))).ToArray();
        var allPreFinalResultIds = allPreFinalResults.Select(result => result.String("id") ?? string.Empty).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allFinalResultIds = allFinalResults.Select(result => result.String("id") ?? string.Empty).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var preFinalSplits = splits.Where(split => allPreFinalResultIds.Contains(split.String("result_id") ?? string.Empty)).ToArray();
        var finalSplits = splits.Where(split => allFinalResultIds.Contains(split.String("result_id") ?? string.Empty)).ToArray();
        var preFinalSectors = sectorRows.Where(row => allPreFinalResultIds.Contains(row.String("result_id") ?? string.Empty)).ToArray();
        var finalSectors = sectorRows.Where(row => allFinalResultIds.Contains(row.String("result_id") ?? string.Empty)).ToArray();
        var preFinalSplitWins = RiderTimingStatistics.CountTimingWins(preFinalSplits, allPreFinalResults, preFinalResultIds, useSectorTimes: false);
        var preFinalSectorWins = RiderTimingStatistics.CountTimingWins(preFinalSectors, allPreFinalResults, preFinalResultIds, useSectorTimes: true);
        var finalSplitWins = RiderTimingStatistics.CountTimingWins(finalSplits, allFinalResults, finalResultIds, useSectorTimes: false);
        var finalSectorWins = RiderTimingStatistics.CountTimingWins(finalSectors, allFinalResults, finalResultIds, useSectorTimes: true);
        var qualificationSpeeds = targetSplits.Where(split => qualificationResultIds.Contains(split.String("result_id") ?? string.Empty)).Select(split => split.Decimal("speed")).Where(value => value is not null).Select(value => value!.Value).ToArray();
        var finalSpeeds = targetSplits.Where(split => finalResultIds.Contains(split.String("result_id") ?? string.Empty)).Select(split => split.Decimal("speed")).Where(value => value is not null).Select(value => value!.Value).ToArray();

        return new RiderStatisticsDto(
            PublicDtoMapping.Long(riderId),
            rider.String("uci_id"),
            rider.String("first_name"),
            rider.String("last_name"),
            rider.DisplayName(),
            rider.String("profile_image_url"),
            rider.String("nation"),
            rider.String("gender"),
            PublicDtoMapping.NullableLong(team?.String("id")),
            team?.String("name"),
            team?.String("code"),
            currentCategory,
            currentPresentation.Age,
            performance.Rating,
            performance.Pace,
            performance.Consistency,
            performance.PaceConfidence,
            AveragePosition(riderResults),
            riderRow.Decimal("points") ?? 0m,
            rankingPosition,
            qualifyingPositions,
            finalPositions,
            riderResults.Length,
            classifiedFinalPositions.Count(position => position == 1),
            classifiedFinalPositions.Count(position => position <= 3),
            classifiedFinalPositions.Count(position => position <= 5),
            classifiedFinalPositions.Count(position => position <= 10),
            splitWins,
            sectorWins,
            preFinalSplitWins,
            preFinalSectorWins,
            finalSplitWins,
            finalSectorWins,
            new RiderSpeedDto(
                BuildSpeedGroup(qualifying, qualificationSpeeds),
                BuildSpeedGroup(finals, finalSpeeds)),
            AveragePositionForType(riderResults, "qualifying"),
            AveragePositionForType(riderResults, "qualifying_1"),
            AveragePositionForType(riderResults, "qualifying_2"),
            AveragePositionForType(riderResults, "semi_final"),
            AveragePositionForType(riderResults, "final"),
            averageSectorTimes,
            AverageSeconds(riderResults.Where(IsCompleted).Select(result => result.Int("race_time_ms"))));
    }

    public async Task<RiderOverviewDto> GetOverviewAsync(
        string riderId,
        IQueryCollection queryString,
        CancellationToken cancellationToken)
    {
        var overviewQuery = OverviewQuery(queryString);
        var context = await _domainQueries.ParseMetricsContextAsync(overviewQuery, _validator, cancellationToken);
        var snapshot = await _ranking.BuildSnapshotAsync(
            context,
            cancellationToken,
            riderId: riderId);
        var rankingPosition = await _ranking.GetPointsPositionAsync(
            context,
            riderId,
            cancellationToken);
        var statisticsTask = GetPublicStatisticsCoreAsync(
            riderId,
            overviewQuery,
            cancellationToken,
            snapshot,
            rankingPosition);
        var roundResultsTask = ListPublicRiderRoundResultsCoreAsync(
            riderId,
            overviewQuery,
            cancellationToken,
            snapshot);
        var trackStatisticsTask = ListPublicTrackStatisticsCoreAsync(
            riderId,
            overviewQuery,
            cancellationToken,
            snapshot);
        var pressurePerformanceTask = _analytics.GetPressurePerformanceAsync(
            riderId,
            overviewQuery,
            cancellationToken);
        var teamHistoryTask = _domainQueries.GetRiderTeamHistoryAsync(
            [riderId],
            cancellationToken: cancellationToken);
        await Task.WhenAll(
            statisticsTask,
            roundResultsTask,
            trackStatisticsTask,
            pressurePerformanceTask,
            teamHistoryTask);

        var sessionResults = new JsonArray(
            snapshot.Results
                .Where(result => string.Equals(result.String("rider_id"), riderId, StringComparison.OrdinalIgnoreCase))
                .Select(result => result.DeepClone())
                .ToArray());

        return new RiderOverviewDto(
            await statisticsTask,
            (await roundResultsTask).Items,
            (await trackStatisticsTask).Items,
            sessionResults,
            JsonSerializerProxy.ToJsonObject(await pressurePerformanceTask),
            RiderTeamHistoryMapper.ToPublicTeamHistoryJson(await teamHistoryTask))
        {
            ChartStatistics = RiderChartStatistics.Build(sessionResults.OfType<JsonObject>(),
                (await roundResultsTask).Items, (await trackStatisticsTask).Items)
        };
    }

    public Task<PublicPagedResponse<RiderRoundResultDto>> ListPublicRiderRoundResultsAsync(
        string riderId,
        IQueryCollection queryString,
        CancellationToken cancellationToken) =>
        ListPublicRiderRoundResultsCoreAsync(riderId, queryString, cancellationToken, null);

    private async Task<PublicPagedResponse<RiderRoundResultDto>> ListPublicRiderRoundResultsCoreAsync(
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
        return PublicPagedResponse<RiderRoundResultDto>.Create(items, page.Page, page.Limit, sorted.Count);
    }

    public Task<PublicPagedResponse<RiderTrackStatisticsDto>> ListPublicTrackStatisticsAsync(
        string riderId,
        IQueryCollection queryString,
        CancellationToken cancellationToken) =>
        ListPublicTrackStatisticsCoreAsync(riderId, queryString, cancellationToken, null);

    private async Task<PublicPagedResponse<RiderTrackStatisticsDto>> ListPublicTrackStatisticsCoreAsync(
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
        return PublicPagedResponse<RiderTrackStatisticsDto>.Create(items, page.Page, page.Limit, sorted.Count);
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

    private JsonObject BuildRiderAggregate(
        string riderId,
        IReadOnlyList<JsonObject> results,
        IReadOnlyDictionary<string, decimal> pointsByResultId,
        IReadOnlyDictionary<string, RiderPerformanceMetricValues> performanceByRiderId,
        RiderMetricsContext context)
    {
        var rider = results.Select(result => result.Object("riders")).FirstOrDefault(row => row is not null)?.DeepClone()?.AsObject() ?? new JsonObject();
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
            .Where(DomainQueryService.IsFinalResult)
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
        rider["starts"] = results.Count;
        rider["teams"] = DomainQueryService.BuildTeamHistory(results);
        rider["id"] = riderId;
        return rider;
    }

    private bool MatchesMetricFilters(JsonObject row, IQueryCollection queryString)
    {
        return NumberWithinNullable(row.Decimal("rating"), _validator.Decimal(queryString, "min_rating"), _validator.Decimal(queryString, "max_rating")) &&
            NumberWithinNullable(row.Decimal("pace"), _validator.Decimal(queryString, "min_pace"), _validator.Decimal(queryString, "max_pace")) &&
            NumberWithinNullable(row.Decimal("consistency"), _validator.Decimal(queryString, "min_consistency"), _validator.Decimal(queryString, "max_consistency"));
    }

    private async Task<IReadOnlyList<JsonObject>> BuildVenueRowsAsync(string riderId, RiderMetricsContext context, CancellationToken cancellationToken)
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

    private static string[] ResultIds(IEnumerable<JsonObject> results)
    {
        return results.Select(result => result.String("id")).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!).ToArray();
    }

    private static decimal SumPoints(IEnumerable<JsonObject> results, IReadOnlyDictionary<string, decimal> pointsByResultId)
    {
        return results.Sum(result => pointsByResultId.TryGetValue(result.String("id") ?? string.Empty, out var points) ? points : 0);
    }

    private static decimal? Average(IEnumerable<decimal?> values)
    {
        var numbers = values.Where(value => value is not null).Select(value => value!.Value).ToArray();
        return numbers.Length == 0 ? null : numbers.Average();
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

    private static IReadOnlyList<JsonObject> SortRiderRows(IReadOnlyList<JsonObject> rows, string sort, bool descending)
    {
        if (sort is not ("rating" or "pace" or "consistency"))
        {
            return JsonSorting.Sort(rows, sort, descending);
        }

        return descending
            ? rows.OrderByDescending(row => row.Decimal(sort) ?? decimal.MinValue).ToArray()
            : rows.OrderBy(row => row.Decimal(sort) ?? decimal.MaxValue).ToArray();
    }

    private async Task<IReadOnlyList<JsonObject>> FetchSplitsAsync(
        IReadOnlyList<string> resultIds,
        CancellationToken cancellationToken)
    {
        var rows = new List<JsonObject>();
        foreach (var batch in resultIds.Chunk(150))
        {
            var response = await _supabase.GetAsync(
                new SupabaseQuery("time_splits")
                    .Select("id,result_id,split_index,race_time,speed")
                    .In("result_id", batch)
                    .Order("result_id", false)
                    .Order("split_index", false)
                    .Limit(5000),
                cancellationToken: cancellationToken);
            rows.AddRange(response.Data);
        }

        return rows;
    }

    internal static IReadOnlyList<JsonObject> BuildSectorRows(
        IReadOnlyList<JsonObject> splits, IReadOnlyList<JsonObject>? results = null) =>
        RiderTimingStatistics.BuildSectorRows(splits, results);

    private static SessionPositionDto ToSessionPosition(JsonObject result)
    {
        var session = result.Object("sessions");
        return new SessionPositionDto(
            PublicDtoMapping.Long(session?.String("round_id") ?? session?.Object("rounds")?.String("id")),
            PublicDtoMapping.Long(result.String("session_id") ?? session?.String("id")),
            PublicApiCodes.ToPublicSessionType(session?.String("session_type")) ?? "unknown",
            result.Int("position"));
    }

    private static IReadOnlyList<JsonObject> DistinctSessions(IEnumerable<JsonObject> results)
    {
        return results
            .Select(result => result.Object("sessions"))
            .Where(session => session?.String("id") is not null)
            .Select(session => session!)
            .GroupBy(session => session.String("id")!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static QueryCollection OverviewQuery(IQueryCollection queryString)
    {
        var values = queryString.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in OverviewPresentationParameters)
        {
            values.Remove(parameter);
        }

        values["page"] = "1";
        values["limit"] = "150";
        return new QueryCollection(values);
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

    private static RiderPerformanceMetricValues PerformanceFromRankingRow(JsonObject row) =>
        new(
            row.Decimal("rating"),
            row.Decimal("raw_rating"),
            row.Decimal("pace"),
            row.Decimal("raw_pace"),
            row.Decimal("pace_confidence") ?? 0m,
            row.Int("pace_used_sessions") ?? 0,
            row.Decimal("consistency"),
            row.Decimal("consistency_confidence") ?? 0m,
            row.Int("consistency_used_runs") ?? 0,
            row.Int("consistency_used_sectors") ?? 0)
        {
            PaceConfidenceStatus = row.String("pace_confidence_status") ?? "Low",
            PaceUsedFinals = row.Int("pace_used_finals") ?? 0,
            PaceTotalEvidence = row.Decimal("pace_total_evidence") ?? 0m,
            PaceFinalEvidence = row.Decimal("pace_final_evidence") ?? 0m,
            ResultScore = row.Decimal("result_score"),
            ResultScoreConfidence = row.Decimal("result_score_confidence") ?? 0m,
            ResultScoreUsedSessions = row.Int("result_score_used_sessions") ?? 0
        };

    private async Task<CurrentRiderPresentation> GetCurrentPresentationAsync(
        string riderId,
        CancellationToken cancellationToken)
    {
        var riderTask = _domainQueries.FindByIdAsync(
            "riders",
            riderId,
            cancellationToken: cancellationToken);
        var teamHistoryTask = _domainQueries.GetRiderTeamHistoryAsync(
            [riderId],
            cancellationToken: cancellationToken);
        await Task.WhenAll(riderTask, teamHistoryTask);
        var rider = await riderTask;
        if (rider is null)
        {
            throw new ApiException(
                StatusCodes.Status404NotFound,
                "RIDER_NOT_FOUND",
                "Rider was not found.");
        }

        var team = (await teamHistoryTask)
            .OrderByDescending(row => row.String("valid_from"), StringComparer.Ordinal)
            .Select(row => row.Object("teams"))
            .FirstOrDefault(row => row is not null);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return new CurrentRiderPresentation(
            rider,
            team,
            RiderCategoryResolver.CurrentCategory(rider, today),
            RiderCategoryResolver.CalculateAge(
                PublicDtoMapping.Date(rider.String("birth_date")),
                today));
    }

    private static RiderStatisticsDto EmptyPublicStatistics(
        string riderId,
        CurrentRiderPresentation presentation) =>
        new(
            PublicDtoMapping.Long(riderId),
            presentation.Rider.String("uci_id"),
            presentation.Rider.String("first_name"),
            presentation.Rider.String("last_name"),
            presentation.Rider.DisplayName(),
            presentation.Rider.String("profile_image_url"),
            presentation.Rider.String("nation"),
            presentation.Rider.String("gender"),
            PublicDtoMapping.NullableLong(presentation.Team?.String("id")),
            presentation.Team?.String("name"),
            presentation.Team?.String("code"),
            presentation.CurrentCategory,
            presentation.Age,
            null,
            null,
            null,
            0m,
            null,
            0m,
            null,
            [],
            [],
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            new RiderSpeedDto(
                new SpeedGroupDto(null, null),
                new SpeedGroupDto(null, null)),
            null,
            null,
            null,
            null,
            null,
            [],
            null);

    private static string? SessionType(JsonObject result) => result.Object("sessions")?.String("session_type");

    private static string ResultRoundId(JsonObject result)
    {
        var session = result.Object("sessions");
        return session?.String("round_id") ?? session?.Object("rounds")?.String("id") ?? string.Empty;
    }

    private static DateOnly? ResultDate(JsonObject result)
    {
        var session = result.Object("sessions");
        return PublicDtoMapping.Date(session?.String("session_date") ?? session?.Object("rounds")?.String("start_date"));
    }

    private static decimal? AveragePosition(IEnumerable<JsonObject> results)
    {
        var positions = results
            .Select(result => result.Int("position"))
            .Where(position => position is > 0)
            .Select(position => (decimal)position!.Value)
            .ToArray();
        return positions.Length == 0 ? null : Math.Round(positions.Average(), 2);
    }

    internal static decimal? AveragePositionForType(IEnumerable<JsonObject> results, string publicSessionType)
    {
        return AveragePosition(results.Where(result =>
            string.Equals(
                PublicApiCodes.ToPublicSessionType(SessionType(result)),
                publicSessionType,
                StringComparison.OrdinalIgnoreCase)));
    }

    private static SpeedGroupDto BuildSpeedGroup(
        IReadOnlyList<JsonObject> results,
        IReadOnlyList<decimal> splitSpeeds)
    {
        var topSpeeds = results
            .Select(result => result.Decimal("top_speed"))
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToArray();
        return new SpeedGroupDto(
            topSpeeds.Length == 0 ? null : topSpeeds.Max(),
            splitSpeeds.Count == 0 ? null : Math.Round(splitSpeeds.Average(), 3));
    }

    private static bool IsCompleted(JsonObject result)
    {
        var status = result.String("status");
        return result.Int("race_time_ms") is > 0 &&
            !string.Equals(status, "DNF", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "DNS", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "DSQ", StringComparison.OrdinalIgnoreCase);
    }

    private static decimal? AverageSeconds(IEnumerable<int?> milliseconds)
    {
        var values = milliseconds.Where(value => value is > 0).Select(value => value!.Value).ToArray();
        return values.Length == 0 ? null : Math.Round(values.Average(value => (decimal)value) / 1000m, 3);
    }

    internal static RiderRoundResultDto ToRoundResult(
        string roundId,
        IReadOnlyList<JsonObject> results,
        RiderPerformanceMetricValues? performance)
    {
        var session = results[0].Object("sessions");
        var round = session?.Object("rounds");
        var track = round?.Object("tracks");
        var competitionClass = round?.Object("competition_levels") ?? round?.Object("competition_class");
        var condition = round?.Object("conditions") ?? session?.Object("conditions");
        var trackId = PublicDtoMapping.NullableLong(round?.String("track_id") ?? track?.String("id"));

        int? Position(string sessionType) => results
            .Where(result => string.Equals(PublicApiCodes.ToPublicSessionType(SessionType(result)), sessionType, StringComparison.OrdinalIgnoreCase))
            .Select(result => result.Int("position"))
            .FirstOrDefault(position => position is > 0);

        return new RiderRoundResultDto(
            PublicDtoMapping.Long(roundId),
            round?.Int("round_number"),
            round?.Int("season_year"),
            PublicDtoMapping.NullableLong(round?.String("competition_level_id") ?? round?.String("competition_class_id") ?? competitionClass?.String("id")),
            competitionClass?.String("code"),
            competitionClass?.String("name"),
            PublicDtoMapping.Condition(condition),
            PublicDtoMapping.Date(round?.String("start_date")),
            trackId,
            track?.String("name"),
            trackId,
            track?.String("venue"),
            track?.String("country_code"),
            performance?.Rating,
            performance?.Pace,
            performance?.Consistency,
            performance?.PaceConfidence ?? 0m,
            Position("qualifying"),
            Position("qualifying_1"),
            Position("qualifying_2"),
            Position("semi_final"),
            Position("final"),
            results.Sum(result => result.Decimal("awarded_points") ?? 0m));
    }

    private static RiderTrackStatisticsDto ToTrackStatistics(
        string trackId,
        IReadOnlyList<JsonObject> results,
        RiderPerformanceMetricValues? performance)
    {
        var track = results[0].Object("sessions")?.Object("rounds")?.Object("tracks");
        var id = PublicDtoMapping.Long(trackId);
        return new RiderTrackStatisticsDto(
            id,
            track?.String("name"),
            id,
            track?.String("venue"),
            track?.String("country_code"),
            performance?.Rating,
            performance?.Pace,
            performance?.Consistency,
            performance?.PaceConfidence ?? 0m,
            AveragePositionForType(results, "qualifying"),
            AveragePositionForType(results, "qualifying_1"),
            AveragePositionForType(results, "qualifying_2"),
            AveragePositionForType(results, "semi_final"),
            AveragePositionForType(results, "final"),
            results.Count);
    }

    private static IReadOnlyList<RiderRoundResultDto> SortRoundResults(
        IReadOnlyList<RiderRoundResultDto> rows,
        string sort,
        bool descending)
    {
        Func<RiderRoundResultDto, IComparable?> key = sort switch
        {
            "roundNumber" => row => row.RoundNumber,
            "season" => row => row.Season,
            "rating" => row => row.Rating,
            "pace" => row => row.Pace,
            "consistency" => row => row.Consistency,
            "confidence" => row => row.Confidence,
            "finalPosition" => row => row.FinalPosition,
            "awardedPoints" => row => row.AwardedPoints,
            _ => row => row.RoundStartDate
        };
        return (descending ? rows.OrderByDescending(key) : rows.OrderBy(key))
            .ThenBy(row => row.RoundId)
            .ToArray();
    }

    private static IReadOnlyList<RiderTrackStatisticsDto> SortTrackResults(
        IReadOnlyList<RiderTrackStatisticsDto> rows,
        string sort,
        bool descending)
    {
        Func<RiderTrackStatisticsDto, IComparable?> key = sort switch
        {
            "trackName" => row => row.TrackName,
            "countryCode" => row => row.CountryCode,
            "rating" => row => row.Rating,
            "pace" => row => row.Pace,
            "consistency" => row => row.Consistency,
            "confidence" => row => row.Confidence,
            "averageFinalPosition" => row => row.AverageFinalPosition,
            "startsCount" => row => row.StartsCount,
            _ => row => row.VenueName
        };
        return (descending ? rows.OrderByDescending(key) : rows.OrderBy(key))
            .ThenBy(row => row.TrackId)
            .ToArray();
    }

    private sealed record CurrentRiderPresentation(
        JsonObject Rider,
        JsonObject? Team,
        string? CurrentCategory,
        int? Age);
}
