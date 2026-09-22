using static UCI_Stats_API.Features.Results.ResultTimingProjection;
using static UCI_Stats_API.Features.Results.SessionResultMapper;
using UCI_Stats_API.Features.Results;
using System.Text.Json.Nodes;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Models;

namespace UCI_Stats_API.Services;

public sealed class ResultsService
{
    private const string TimingModeSectors = "sectors";
    private const string TimingModeSplits = "splits";
    private const string PublicResultSelect =
        "*,riders(*,rider_team_history(rider_id,valid_from,valid_to,teams(id,code,name))),teams(*)," +
        "result_points(points_awarded,points_system_id,point_system_stage_id,point_rule_id)," +
        "time_splits(id,result_id,split_index,race_time,speed)," +
        "sessions!inner(*,conditions(*),round_categories!inner(*,rounds(*,tracks(*),competition_levels(*),conditions(*)),categories!inner(*)))";

    private static readonly Dictionary<string, string> ResultSorts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["position"] = "position",
        ["resultId"] = "id",
        ["race_nr"] = "race_nr",
        ["fullName"] = "rider_name",
        ["riderName"] = "rider_name",
        ["rider_name"] = "rider_name",
        ["countryCode"] = "rider_nation",
        ["nation"] = "rider_nation",
        ["status"] = "status",
        ["finalTime"] = "race_time_ms",
        ["race_time_ms"] = "race_time_ms",
        ["finalGapToLeader"] = "gap_ms",
        ["gap_ms"] = "gap_ms",
        ["topSpeed"] = "top_speed",
        ["top_speed"] = "top_speed",
        ["completed_distance"] = "completed_distance",
        ["start_order"] = "race_nr"
    };

    private static readonly Dictionary<string, string> TimeSplitSorts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["split_index"] = "split_index",
        ["race_time_ms"] = "race_time",
        ["sector_time_ms"] = "race_time",
        ["speed"] = "speed",
        ["position"] = "split_index",
        ["time_gap_raw_ms"] = "split_index"
    };

    private readonly ISupabaseRestClient _supabase;
    private readonly QueryValidationService _validator;
    private readonly DomainQueryService _domainQueries;
    private readonly MetricsService _metrics;

    public ResultsService(ISupabaseRestClient supabase, QueryValidationService validator, DomainQueryService domainQueries, MetricsService metrics)
    {
        _supabase = supabase;
        _validator = validator;
        _domainQueries = domainQueries;
        _metrics = metrics;
    }

    public async Task<ListResponse<JsonObject>> ListSessionResultsAsync(string sessionId, IQueryCollection queryString, CancellationToken cancellationToken)
    {
        var page = _validator.Page(queryString);
        var timingMode = TimingMode(queryString);
        var timingSortIndex = TimingSortIndex(queryString);
        var requestedSort = timingSortIndex is null
            ? _validator.Sort(queryString, ResultSorts, "position", "desc")
            : new SortRequest($"time:{timingSortIndex.Value}",
                (_validator.StringAny(queryString, "sortDirection", "sort_direction", "order") ?? "desc").ToLowerInvariant());
        if (requestedSort.Order is not ("asc" or "desc"))
        {
            throw _validator.BadQuery("sortDirection must be asc or desc.");
        }
        var sort = new SortRequest(
            timingSortIndex is null ? ResultSorts[requestedSort.Sort] : requestedSort.Sort,
            requestedSort.Order);
        var minGapMs = _validator.Int(queryString, "min_gap_ms");
        var maxGapMs = _validator.Int(queryString, "max_gap_ms");
        var query = new SupabaseQuery("results")
            .Select(DomainQueryService.ResultSelect)
            .Filter("session_id", "eq", sessionId);

        ApplyResultFilters(query, queryString);

        var riderQuery = _validator.StringAny(queryString, "search", "q");
        var nation = _validator.StringAny(queryString, "countryCode", "country_code", "nation");
        if (!string.IsNullOrWhiteSpace(riderQuery) || !string.IsNullOrWhiteSpace(nation))
        {
            var riderIds = await _domainQueries.FindRiderIdsAsync(riderQuery, nation, cancellationToken: cancellationToken);
            if (riderIds.Count == 0)
            {
                return ResponseFactory.Page(Array.Empty<JsonObject>(), page.Page, page.Limit, 0, sort.Sort, sort.Order);
            }

            query.In("rider_id", riderIds);
        }

        if (timingSortIndex is not null || sort.Sort is "position" or "race_time_ms" or "rider_name" or "rider_nation" or "gap_ms" || minGapMs is not null || maxGapMs is not null)
        {
            var all = await _supabase.GetAsync(query, count: true, cancellationToken: cancellationToken);
            DomainQueryService.NormalizeResultEmbeds(all.Data);
            await _domainQueries.DecorateGapMsAsync(all.Data, cancellationToken: cancellationToken);
            var filtered = FilterByGap(all.Data, minGapMs, maxGapMs);
            if (timingSortIndex is not null)
            {
                await DecorateSessionTimingAsync(sessionId, filtered, timingMode, cancellationToken);
            }
            var sorted = timingSortIndex is not null
                ? SortByTimingPosition(filtered, timingMode, timingSortIndex.Value, sort.Descending)
                : sort.Sort == "position"
                    ? SortByResultPosition(filtered, sort.Descending)
                    : sort.Sort == "race_time_ms"
                        ? SortByRaceTime(filtered, sort.Descending)
                    : JsonSorting.Sort(filtered, InMemoryResultSort(sort.Sort), sort.Descending);
            var pageData = sorted.Skip(page.Offset).Take(page.Limit).ToArray();
            if (timingSortIndex is null)
            {
                await DecorateSessionTimingAsync(sessionId, pageData, timingMode, cancellationToken);
            }
            await _domainQueries.DecorateMissingResultTeamsFromHistoryAsync(
                pageData,
                cancellationToken);
            return ResponseFactory.Page(pageData, page.Page, page.Limit, sorted.Count, sort.Sort, sort.Order);
        }

        query.Order(sort.Sort, sort.Descending)
            .Limit(page.Limit)
            .Offset(page.Offset);

        var response = await _supabase.GetAsync(query, count: true, cancellationToken: cancellationToken);
        DomainQueryService.NormalizeResultEmbeds(response.Data);
        await _domainQueries.DecorateGapMsAsync(response.Data, cancellationToken: cancellationToken);
        await DecorateSessionTimingAsync(sessionId, response.Data, timingMode, cancellationToken);
        await _domainQueries.DecorateMissingResultTeamsFromHistoryAsync(
            response.Data,
            cancellationToken);
        return ResponseFactory.Page(response.Data, page.Page, page.Limit, response.Total, sort.Sort, sort.Order);
    }

    public async Task<SessionResultsResponseDto> ListPublicSessionResultsAsync(
        string sessionId,
        IQueryCollection queryString,
        CancellationToken cancellationToken)
    {
        var query = new SupabaseQuery("results")
            .Select(PublicResultSelect)
            .Filter("session_id", "eq", sessionId)
            .Order("id", false)
            .Limit(5000);
        return await BuildPublicResultsResponseAsync(query, queryString, cancellationToken);
    }

    public async Task<SessionResultsResponseDto> ListPublicRoundResultsAsync(
        string roundId,
        IQueryCollection queryString,
        CancellationToken cancellationToken)
    {
        var categoryValue = _validator.StringAny(queryString, "category", "categoryCode", "category_code")
            ?? throw _validator.BadQuery("category is required for round results.");
        var sessionTypeValue = _validator.StringAny(queryString, "sessionType", "session_type")
            ?? throw _validator.BadQuery("sessionType is required for round results.");
        string categoryCode;
        string sessionType;
        try
        {
            categoryCode = await _domainQueries.ResolveCategoryCodeAsync(categoryValue, cancellationToken);
            sessionType = await _domainQueries.ResolveSessionTypeCodeAsync(sessionTypeValue, cancellationToken);
        }
        catch (ArgumentException exception)
        {
            throw _validator.BadQuery(exception.Message);
        }

        var query = new SupabaseQuery("results")
            .Select(PublicResultSelect)
            .Filter("sessions.round_id", "eq", roundId)
            .Filter("sessions.session_type", "eq", sessionType)
            .Filter("sessions.round_categories.categories.code", "eq", categoryCode)
            .Order("id", false)
            .Limit(5000);
        return await BuildPublicResultsResponseAsync(query, queryString, cancellationToken);
    }

    private async Task<SessionResultsResponseDto> BuildPublicResultsResponseAsync(
        SupabaseQuery query,
        IQueryCollection queryString,
        CancellationToken cancellationToken)
    {
        var response = await _supabase.GetAsync(query, cancellationToken: cancellationToken);
        if (response.Data.Count == 0)
        {
            throw new ApiException(StatusCodes.Status404NotFound, "SESSION_RESULTS_NOT_FOUND", "No results match the requested session.");
        }

        DomainQueryService.NormalizeResultEmbeds(response.Data);
        var session = response.Data[0].Object("sessions")
            ?? throw new ApiException(StatusCodes.Status502BadGateway, "SESSION_DATA_MISSING", "Session data is missing from the results package.");

        DomainQueryService.NormalizeSessionEmbed(session);
        await ValidateSessionFiltersAsync(session, queryString, cancellationToken);
        var timeView = TimingMode(queryString);
        var allSplits = ExtractEmbeddedSplits(response.Data);
        DecorateGapMs(response.Data);
        DecorateLoadedTiming(response.Data, allSplits, timeView);

        var filtered = ApplyPublicResultFilters(response.Data, queryString);
        var sorted = SortPublicResults(filtered, queryString, timeView);
        var page = _validator.Page(queryString);
        var pageResults = sorted.Skip(page.Offset).Take(page.Limit).ToArray();
        DecorateMissingResultTeamsFromEmbeddedHistory(pageResults);
        var timingSortIndex = TimingSortIndex(queryString);
        var canonicalPositions = BuildDisplayedPositions(filtered, timeView, timingSortIndex);

        var round = session.Object("rounds");
        var trackLengthM = round?.Object("tracks")?.Decimal("length_m");
        var regularItems = pageResults.Select(result => ToPublicResult(
            result,
            timeView,
            canonicalPositions.GetValueOrDefault(result.String("id") ?? string.Empty),
            trackLengthM)).ToArray();
        var perfectRun = BuildPerfectRun(
            response.Data,
            allSplits,
            timeView,
            trackLengthM);
        var items = perfectRun is null || page.Page > 1
            ? regularItems
            : new[] { perfectRun }.Concat(regularItems).ToArray();

        var condition = session.Object("conditions") ?? round?.Object("conditions");
        var category = session.Object("categories");
        var distinctSplitCount = allSplits
            .Select(split => split.Int("split_index"))
            .Where(index => index is not null)
            .Distinct()
            .Count();
        var summary = new SessionSummaryDto(
            ParseLong(session.String("id")),
            ParseLong(session.String("round_id") ?? round?.String("id")),
            PublicApiCodes.ToPublicSessionType(session.String("session_type")),
            PublicApiCodes.ToPublicCategory(category?.String("code")),
            ToCondition(condition),
            response.Data.Count(IsCompletedResult),
            response.Data.Count,
            distinctSplitCount,
            CountSessionSectors(response.Data, allSplits),
            timeView);

        return new SessionResultsResponseDto(
            summary,
            items,
            page.Page,
            page.Limit,
            sorted.Count,
            sorted.Count == 0 ? 0 : (int)Math.Ceiling(sorted.Count / (double)page.Limit),
            page.Page > 1,
            page.Offset + page.Limit < sorted.Count);
    }

    public async Task<JsonObject> GetResultAsync(string resultId, string[] include, CancellationToken cancellationToken)
    {
        var result = await _domainQueries.FindByIdAsync(
            "results",
            resultId,
            $"{DomainQueryService.ResultSelect},time_splits(*)",
            cancellationToken: cancellationToken);

        if (result is null)
        {
            throw new ApiException(StatusCodes.Status404NotFound, "RESULT_NOT_FOUND", "Result was not found.");
        }

        DomainQueryService.NormalizeResultEmbeds([result]);

        if (result.Object("sessions") is { } session)
        {
            DomainQueryService.NormalizeSessionEmbed(session);
            await _domainQueries.DecorateMissingResultTeamsFromHistoryAsync(
                [result],
                cancellationToken);
        }

        await _domainQueries.DecorateGapMsAsync([result], cancellationToken: cancellationToken);

        if (include.Contains("metrics", StringComparer.OrdinalIgnoreCase) && result.String("rider_id") is { } riderId)
        {
            var context = new RiderMetricsContext(null, null, null, null, null, null, null, null, null, null, null);
            var metrics = await _metrics.CalculateMetricsAsync(riderId, context, cancellationToken);
            result["metrics"] = JsonSerializerProxy.ToJsonObject(metrics);
        }

        return result;
    }

    public async Task<ListResponse<JsonObject>> ListTimeSplitsAsync(string resultId, IQueryCollection queryString, CancellationToken cancellationToken)
    {
        var sort = _validator.Sort(queryString, TimeSplitSorts, "split_index", "asc");
        var response = await _supabase.GetAsync(
            new SupabaseQuery("time_splits")
                .Select("*")
                .Filter("result_id", "eq", resultId)
                .Order(TimeSplitSorts[sort.Sort], sort.Descending),
            count: true,
            cancellationToken: cancellationToken);

        DecorateSplitRows(response.Data);
        var data = sort.Sort == "sector_time_ms"
            ? JsonSorting.Sort(response.Data, "sector_time_ms", sort.Descending)
            : response.Data;

        return ResponseFactory.Page(data, 1, Math.Max(1, data.Count), response.Total, sort.Sort, sort.Order);
    }

    public void ApplyResultFilters(SupabaseQuery query, IQueryCollection queryString)
    {
        query.Filter("status", "eq", _validator.String(queryString, "status"));
        query.Filter("race_time_ms", "gte", _validator.Int(queryString, "min_race_time_ms"));
        query.Filter("race_time_ms", "lte", _validator.Int(queryString, "max_race_time_ms"));
        query.Filter("top_speed", "gte", _validator.Decimal(queryString, "min_top_speed"));
        query.Filter("top_speed", "lte", _validator.Decimal(queryString, "max_top_speed"));
    }

    private static IReadOnlyList<JsonObject> FilterByGap(IReadOnlyList<JsonObject> rows, int? minGapMs, int? maxGapMs)
    {
        if (minGapMs is null && maxGapMs is null)
        {
            return rows;
        }

        return rows
            .Where(row =>
            {
                var gap = row.Int("gap_ms");
                return gap is not null &&
                    (minGapMs is null || gap.Value >= minGapMs.Value) &&
                    (maxGapMs is null || gap.Value <= maxGapMs.Value);
            })
            .ToArray();
    }

    private IReadOnlyList<JsonObject> ApplyPublicResultFilters(
        IReadOnlyList<JsonObject> rows,
        IQueryCollection queryString)
    {
        var status = _validator.String(queryString, "status");
        var minRaceTime = _validator.Int(queryString, "min_race_time_ms");
        var maxRaceTime = _validator.Int(queryString, "max_race_time_ms");
        var minSpeed = _validator.Decimal(queryString, "min_top_speed");
        var maxSpeed = _validator.Decimal(queryString, "max_top_speed");
        var minGap = _validator.Int(queryString, "min_gap_ms");
        var maxGap = _validator.Int(queryString, "max_gap_ms");
        var search = _validator.StringAny(queryString, "search", "q");
        var nation = _validator.StringAny(queryString, "countryCode", "country_code", "nation");

        return rows.Where(result =>
        {
            var rider = result.Object("riders");
            var raceTime = result.Int("race_time_ms");
            var speed = result.Decimal("top_speed");
            var gap = result.Int("gap_ms");
            return (string.IsNullOrWhiteSpace(status) || string.Equals(result.String("status"), status, StringComparison.OrdinalIgnoreCase)) &&
                (minRaceTime is null || raceTime >= minRaceTime) &&
                (maxRaceTime is null || raceTime <= maxRaceTime) &&
                (minSpeed is null || speed >= minSpeed) &&
                (maxSpeed is null || speed <= maxSpeed) &&
                (minGap is null || gap >= minGap) &&
                (maxGap is null || gap <= maxGap) &&
                (string.IsNullOrWhiteSpace(nation) || string.Equals(rider?.String("nation"), nation, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(search) || new[]
                {
                    rider?.String("first_name"), rider?.String("last_name"), rider?.String("uci_id"),
                    rider?.String("uci_code"), rider?.DisplayName()
                }.Any(value => value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true));
        }).ToArray();
    }

    private IReadOnlyList<JsonObject> SortPublicResults(
        IReadOnlyList<JsonObject> rows,
        IQueryCollection queryString,
        string timingMode)
    {
        var timingIndex = TimingSortIndex(queryString);
        if (timingIndex is not null)
        {
            var direction = (_validator.StringAny(queryString, "sortDirection", "sort_direction", "order") ?? "desc").ToLowerInvariant();
            if (direction is not ("asc" or "desc"))
            {
                throw _validator.BadQuery("sortDirection must be asc or desc.");
            }

            return SortByTimingPosition(rows, timingMode, timingIndex.Value, direction == "desc");
        }

        var requested = _validator.Sort(queryString, ResultSorts, "position", "desc");
        var sort = ResultSorts[requested.Sort];
        if (sort == "race_time_ms")
        {
            return SortByRaceTime(rows, requested.Descending);
        }

        var regular = rows.Where(result => ResultStatusOrder(result) == 0).ToArray();
        IReadOnlyList<JsonObject> sortedRegular = sort switch
        {
            "position" => SortByResultPosition(regular, requested.Descending),
            _ => JsonSorting.Sort(regular, InMemoryResultSort(sort), requested.Descending)
        };

        return sortedRegular.Concat(rows
            .Where(result => ResultStatusOrder(result) > 0)
            .OrderBy(ResultStatusOrder)
            .ThenBy(result => result.Object("riders")?.DisplayName() ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            .ToArray();
    }

    private static IReadOnlyList<JsonObject> ExtractEmbeddedSplits(IReadOnlyList<JsonObject> results)
    {
        return results.SelectMany(result =>
        {
            var resultId = result.String("id");
            var splits = result.Array("time_splits")?.OfType<JsonObject>().ToArray() ?? [];
            foreach (var split in splits)
            {
                split["result_id"] ??= resultId;
            }
            return splits;
        }).ToArray();
    }

    private static void DecorateMissingResultTeamsFromEmbeddedHistory(
        IReadOnlyList<JsonObject> results)
    {
        foreach (var result in results.Where(result => result.Object("teams") is null))
        {
            var rider = result.Object("riders");
            var riderId = result.String("rider_id") ?? rider?.String("id");
            var resultDate = PublicDtoMapping.Date(
                result.Object("sessions")?.String("session_date") ??
                result.Object("sessions")?.Object("rounds")?.String("start_date"));
            if (rider is null || string.IsNullOrWhiteSpace(riderId) || resultDate is null)
            {
                continue;
            }

            var history = rider.Array("rider_team_history")?.OfType<JsonObject>() ?? [];
            var team = DomainQueryService.FindTeamPeriodOrNearest(history, riderId, resultDate.Value)?
                .Object("teams");
            if (team is null)
            {
                continue;
            }

            result["team_id"] = team["id"]?.DeepClone();
            result["teams"] = team.DeepClone();
        }
    }

    private static void DecorateGapMs(IReadOnlyList<JsonObject> results)
    {
        var bestTime = results
            .Where(IsCompletedResult)
            .Select(result => result.Int("race_time_ms"))
            .Where(time => time is > 0)
            .DefaultIfEmpty()
            .Min();
        foreach (var result in results)
        {
            var raceTime = result.Int("race_time_ms");
            result["gap_ms"] = bestTime is > 0 && raceTime is > 0 && IsCompletedResult(result)
                ? raceTime.Value - bestTime.Value
                : null;
        }
    }

    private static void DecorateLoadedTiming(
        IReadOnlyList<JsonObject> results,
        IReadOnlyList<JsonObject> splits,
        string timingMode)
    {
        var timingsByResult = BuildSectorsByResultId(
            splits,
            results,
            includeFinishSector: timingMode == TimingModeSectors);
        var allTimings = timingsByResult.Values.SelectMany(value => value).ToArray();
        var eligibleIds = results
            .Where(IsEligiblePerfectRunResult)
            .Select(result => result.String("id"))
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var eligibleTimings = timingsByResult
            .Where(pair => eligibleIds.Contains(pair.Key))
            .SelectMany(pair => pair.Value)
            .ToArray();
        var references = BuildTimingReferences(
            eligibleTimings,
            timing => timingMode == TimingModeSectors ? timing.SectorTimeMs : timing.RaceTimeMs);
        var leaderTimes = BuildLeaderTimes(
            results,
            allTimings,
            timing => timingMode == TimingModeSectors ? timing.SectorTimeMs : timing.RaceTimeMs);
        var statusOrders = results
            .Where(result => !string.IsNullOrWhiteSpace(result.String("id")))
            .ToDictionary(result => result.String("id")!, ResultStatusOrder, StringComparer.OrdinalIgnoreCase);
        var positions = timingMode == TimingModeSectors
            ? BuildSectorPositions(allTimings, statusOrders)
            : BuildSplitPositions(allTimings, statusOrders);

        foreach (var result in results)
        {
            if (!timingsByResult.TryGetValue(result.String("id") ?? string.Empty, out var timings))
            {
                result[timingMode] = new JsonArray();
                continue;
            }

            result[timingMode] = new JsonArray(timings.Select(timing =>
                timingMode == TimingModeSectors
                    ? ToSectorJson(timing, references, leaderTimes, positions)
                    : ToSplitJson(timing, references, leaderTimes, positions)).ToArray());
        }
    }

    private static string InMemoryResultSort(string sort)
    {
        return sort;
    }

    internal static IReadOnlyList<JsonObject> SortByResultPosition(
        IReadOnlyList<JsonObject> rows,
        bool descending)
    {
        var classified = rows
            .Where(row => ResultStatusOrder(row) == 0);
        var orderedClassified = descending
            ? classified.OrderBy(row => row.Int("position") ?? int.MaxValue)
            : classified.OrderByDescending(row => row.Int("position") ?? int.MinValue);

        return orderedClassified
            .Concat(rows
                .Where(row => ResultStatusOrder(row) > 0)
                .OrderBy(ResultStatusOrder)
                .ThenBy(row => row.Object("riders")?.DisplayName() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => ParseLong(row.String("id"))))
            .ToArray();
    }

    private static int ResultStatusOrder(JsonObject result)
    {
        return result.String("status")?.Trim().ToUpperInvariant() switch
        {
            "DNF" => 1,
            "DNS" => 2,
            "DSQ" => 3,
            _ when result.Int("position") is > 0 => 0,
            _ => 4
        };
    }

    private static bool HasInvalidStatus(JsonObject result) =>
        result.String("status")?.Trim().ToUpperInvariant() is "DNF" or "DNS" or "DSQ";

    private string TimingMode(IQueryCollection queryString)
    {
        var mode = (_validator.StringAny(queryString, "timeView", "time_view", "timing_mode") ?? TimingModeSectors).ToLowerInvariant();
        return mode switch
        {
            "sector" or TimingModeSectors => TimingModeSectors,
            "split" or TimingModeSplits => TimingModeSplits,
            _ => throw _validator.BadQuery("timeView must be sectors or splits.")
        };
    }

    private int? TimingSortIndex(IQueryCollection queryString)
    {
        var value = _validator.StringAny(queryString, "sortBy", "sort_by", "sort");
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith("time:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return int.TryParse(value[5..], out var index) && index > 0
            ? index
            : throw _validator.BadQuery("Timing sort must use time:{positiveIndex}.");
    }

    internal static IReadOnlyList<JsonObject> SortByTimingPosition(
        IReadOnlyList<JsonObject> rows,
        string timingMode,
        int timingIndex,
        bool descending)
    {
        var positionProperty = timingMode == TimingModeSectors
            ? "sector_position"
            : "split_position";
        int? Position(JsonObject row) => row.Array(timingMode)?
            .OfType<JsonObject>()
            .FirstOrDefault(time => time.Int("split_index") == timingIndex)?
            .Int(positionProperty);

        var withPosition = rows.Where(row => Position(row) is > 0 && !HasInvalidStatus(row));
        var ordered = descending
            ? withPosition.OrderBy(row => Position(row))
            : withPosition.OrderByDescending(row => Position(row));

        var slowest = rows
            .Where(row => Position(row) is null or <= 0 || HasInvalidStatus(row))
            .OrderBy(ResultStatusOrder)
            .ThenByDescending(row => Position(row) ?? int.MaxValue)
            .ThenBy(row => row.Object("riders")?.DisplayName() ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        return (descending ? ordered.Concat(slowest) : slowest.Concat(ordered))
            .ToArray();
    }

    internal static IReadOnlyList<JsonObject> SortByRaceTime(
        IReadOnlyList<JsonObject> rows,
        bool descending)
    {
        var withTime = rows.Where(row => row.Int("race_time_ms") is > 0 && !HasInvalidStatus(row));
        var ordered = descending
            ? withTime.OrderBy(row => row.Int("race_time_ms"))
            : withTime.OrderByDescending(row => row.Int("race_time_ms"));

        var withoutTime = rows
            .Where(row => row.Int("race_time_ms") is null or <= 0 || HasInvalidStatus(row))
            .OrderBy(ResultStatusOrder)
            .ThenByDescending(row => row.Int("race_time_ms") ?? int.MaxValue)
            .ThenBy(row => row.Object("riders")?.DisplayName() ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        return (descending ? ordered.Concat(withoutTime) : withoutTime.Concat(ordered))
            .ToArray();
    }

    private static IReadOnlyDictionary<string, int> BuildDisplayedPositions(
        IReadOnlyList<JsonObject> rows,
        string timingMode,
        int? timingSortIndex)
    {
        var canonical = timingSortIndex is not null
            ? SortByTimingPosition(rows, timingMode, timingSortIndex.Value, descending: true)
            : SortByResultPosition(rows, descending: true);
        var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < canonical.Count; index++)
        {
            var result = canonical[index];
            var id = result.String("id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            if (timingSortIndex is not null)
            {
                var positionProperty = timingMode == TimingModeSectors ? "sector_position" : "split_position";
                var timingPosition = result.Array(timingMode)?.OfType<JsonObject>()
                    .FirstOrDefault(time => time.Int("split_index") == timingSortIndex.Value)?
                    .Int(positionProperty);
                positions[id] = timingPosition is > 0 ? timingPosition.Value : index + 1;
            }
            else
            {
                positions[id] = result.Int("position") is > 0 ? result.Int("position")!.Value : index + 1;
            }
        }

        return positions;
    }

    private async Task DecorateSessionTimingAsync(string sessionId, IReadOnlyList<JsonObject> pageResults, string timingMode, CancellationToken cancellationToken)
    {
        if (timingMode == TimingModeSplits)
        {
            await DecorateSessionSplitsAsync(sessionId, pageResults, cancellationToken);
            return;
        }

        await DecorateSessionSectorsAsync(sessionId, pageResults, cancellationToken);
    }

    private async Task DecorateSessionSectorsAsync(string sessionId, IReadOnlyList<JsonObject> pageResults, CancellationToken cancellationToken)
    {
        if (pageResults.Count == 0)
        {
            return;
        }

        var sessionResults = await _supabase.GetAsync(
            new SupabaseQuery("results")
                .Select("id,position,race_time_ms,is_classified,status")
                .Filter("session_id", "eq", sessionId)
                .Limit(5000),
            cancellationToken: cancellationToken);

        var resultIds = sessionResults.Data
            .Select(result => result.String("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToArray();

        if (resultIds.Length == 0)
        {
            foreach (var result in pageResults)
            {
                result["sectors"] = new JsonArray();
            }

            return;
        }

        var splits = await _supabase.GetAsync(
            new SupabaseQuery("time_splits")
                .Select("id,result_id,split_index,race_time,speed")
                .In("result_id", resultIds)
                .Limit(5000),
            cancellationToken: cancellationToken);

        var sectorsByResultId = BuildSectorsByResultId(
            splits.Data,
            sessionResults.Data,
            includeFinishSector: true);
        var eligibleResultIds = sessionResults.Data
            .Where(IsEligiblePerfectRunResult)
            .Select(result => result.String("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var eligibleSectors = sectorsByResultId
            .Where(pair => eligibleResultIds.Contains(pair.Key))
            .SelectMany(pair => pair.Value)
            .ToArray();
        var sectorReferences = BuildTimingReferences(
            eligibleSectors,
            sector => sector.SectorTimeMs);
        var allSectors = sectorsByResultId.Values.SelectMany(value => value).ToArray();
        var leaderSectorTimes = BuildLeaderTimes(
            sessionResults.Data,
            allSectors,
            sector => sector.SectorTimeMs);
        var statusOrders = sessionResults.Data
            .Where(result => !string.IsNullOrWhiteSpace(result.String("id")))
            .ToDictionary(result => result.String("id")!, ResultStatusOrder, StringComparer.OrdinalIgnoreCase);
        var sectorPositions = BuildSectorPositions(allSectors, statusOrders);

        foreach (var result in pageResults)
        {
            var resultId = result.String("id");
            if (resultId is null || !sectorsByResultId.TryGetValue(resultId, out var sectors))
            {
                result["sectors"] = new JsonArray();
                continue;
            }

            result["sectors"] = new JsonArray(sectors
                .Select(sector => ToSectorJson(
                    sector,
                    sectorReferences,
                    leaderSectorTimes,
                    sectorPositions))
                .ToArray());
        }
    }

    private async Task DecorateSessionSplitsAsync(string sessionId, IReadOnlyList<JsonObject> pageResults, CancellationToken cancellationToken)
    {
        if (pageResults.Count == 0)
        {
            return;
        }

        var sessionResults = await _supabase.GetAsync(
            new SupabaseQuery("results")
                .Select("id,position,race_time_ms,is_classified,status")
                .Filter("session_id", "eq", sessionId)
                .Limit(5000),
            cancellationToken: cancellationToken);

        var resultIds = sessionResults.Data
            .Select(result => result.String("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToArray();

        if (resultIds.Length == 0)
        {
            foreach (var result in pageResults)
            {
                result["splits"] = new JsonArray();
            }

            return;
        }

        var splits = await _supabase.GetAsync(
            new SupabaseQuery("time_splits")
                .Select("id,result_id,split_index,race_time,speed")
                .In("result_id", resultIds)
                .Limit(5000),
            cancellationToken: cancellationToken);

        var splitsByResultId = BuildSectorsByResultId(splits.Data);
        var eligibleResultIds = sessionResults.Data
            .Where(IsEligiblePerfectRunResult)
            .Select(result => result.String("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var eligibleSplits = splitsByResultId
            .Where(pair => eligibleResultIds.Contains(pair.Key))
            .SelectMany(pair => pair.Value)
            .ToArray();
        var splitReferences = BuildTimingReferences(
            eligibleSplits,
            split => split.RaceTimeMs);
        var allSplits = splitsByResultId.Values.SelectMany(value => value).ToArray();
        var leaderSplitTimes = BuildLeaderTimes(
            sessionResults.Data,
            allSplits,
            split => split.RaceTimeMs);
        var statusOrders = sessionResults.Data
            .Where(result => !string.IsNullOrWhiteSpace(result.String("id")))
            .ToDictionary(result => result.String("id")!, ResultStatusOrder, StringComparer.OrdinalIgnoreCase);
        var splitPositions = BuildSplitPositions(allSplits, statusOrders);

        foreach (var result in pageResults)
        {
            var resultId = result.String("id");
            if (resultId is null || !splitsByResultId.TryGetValue(resultId, out var resultSplits))
            {
                result["splits"] = new JsonArray();
                continue;
            }

            result["splits"] = new JsonArray(resultSplits
                .Select(split => ToSplitJson(
                    split,
                    splitReferences,
                    leaderSplitTimes,
                    splitPositions))
                .ToArray());
        }
    }

    internal static SessionResultDto? BuildPerfectRun(
        IReadOnlyList<JsonObject> results, IReadOnlyList<JsonObject> splits,
        string timeView, decimal? trackLengthM = null) =>
        ResultTimingProjection.BuildPerfectRun(results, splits, timeView, trackLengthM);

    private async Task ValidateSessionFiltersAsync(JsonObject session, IQueryCollection queryString, CancellationToken cancellationToken)
    {
        var categories = _validator.CsvAny(queryString, "category", "categoryCode", "category_code");
        if (categories.Length > 1)
        {
            throw _validator.BadQuery("A session results endpoint accepts at most one category.");
        }

        if (categories.Length == 1)
        {
            string expected;
            try
            {
                expected = await _domainQueries.ResolveCategoryCodeAsync(categories[0], cancellationToken);
            }
            catch (ArgumentException)
            {
                throw _validator.BadQuery($"Unsupported category '{categories[0]}'.");
            }

            if (!string.Equals(session.Object("categories")?.String("code"), expected, StringComparison.OrdinalIgnoreCase))
            {
                throw _validator.BadQuery("The requested category does not match this session.");
            }
        }

        var sessionTypes = _validator.CsvAny(queryString, "sessionType", "session_type");
        if (sessionTypes.Length > 1)
        {
            throw _validator.BadQuery("A session results endpoint accepts at most one sessionType.");
        }

        if (sessionTypes.Length == 1)
        {
            string expected;
            try
            {
                expected = await _domainQueries.ResolveSessionTypeCodeAsync(sessionTypes[0], cancellationToken);
            }
            catch (ArgumentException)
            {
                throw _validator.BadQuery($"Unsupported sessionType '{sessionTypes[0]}'.");
            }

            if (!string.Equals(session.String("session_type"), expected, StringComparison.OrdinalIgnoreCase))
            {
                throw _validator.BadQuery("The requested sessionType does not match this session.");
            }
        }
    }

    internal static SessionResultDto ToPublicResult(
        JsonObject result, string timeView, int? rankingPosition, decimal? trackLengthM = null) =>
        SessionResultMapper.ToPublicResult(result, timeView, rankingPosition, trackLengthM);

    internal static decimal? CalculateAverageSpeedKmh(decimal? trackLengthM, int? raceTimeMs) =>
        SessionResultMapper.CalculateAverageSpeedKmh(trackLengthM, raceTimeMs);

    internal static bool IsCompletedResult(JsonObject result) =>
        SessionResultMapper.IsCompletedResult(result);

    private static ConditionDto? ToCondition(JsonObject? condition)
    {
        if (condition is null)
        {
            return null;
        }

        var name = condition.String("name");
        return new ConditionDto(
            NullableLong(condition.String("id")),
            name?.Trim().ToLowerInvariant().Replace(' ', '_'),
            name);
    }

    private static bool IsClassifiedResult(JsonObject result)
    {
        var status = result.String("status");
        return (result.Bool("is_classified") == true || result.Int("position") is > 0) &&
            !string.Equals(status, "DNF", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "DNS", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "DSQ", StringComparison.OrdinalIgnoreCase);
    }

    private static void DecorateSplitRows(IReadOnlyList<JsonObject> rows)
    {
        var ordered = rows.OrderBy(row => row.Int("split_index") ?? 0).ToArray();
        int? previousRaceTime = null;
        foreach (var row in ordered)
        {
            var raceTime = row.Int("race_time");
            row["race_time_ms"] = raceTime;
            row["sector_time_ms"] = raceTime is null ? null : raceTime - (previousRaceTime ?? 0);
            previousRaceTime = raceTime;
        }
    }

}
