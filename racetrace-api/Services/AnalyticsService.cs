using RaceTrace.Api.Features.Metrics;
using RaceTrace.Api.Features.Analytics;
using RaceTrace.Api.Features.Results;
using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Services;

public sealed class AnalyticsService
{

    private readonly QueryValidationService _validator;
    private readonly DomainQueryService _domainQueries;
    private readonly PaceService _pace;
    private readonly TimeProvider _timeProvider;

    public AnalyticsService(
        QueryValidationService validator,
        DomainQueryService domainQueries,
        PaceService pace,
        TimeProvider timeProvider)
    {
        _validator = validator;
        _domainQueries = domainQueries;
        _pace = pace;
        _timeProvider = timeProvider;
    }

    public async Task<RoundProfileResponseDto> GetRoundProfileAsync(
        IQueryCollection queryString,
        CancellationToken cancellationToken)
    {
        var requestedCategory = _validator.StringAny(queryString, "category", "categoryCode", "category_code");
        var openCategories = RoundResultSelection.Categories(requestedCategory ?? "elite_men");
        var isOpen = openCategories.Length > 1;
        if (isOpen)
        {
            var values = queryString.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            values.Remove("categoryCode");
            values.Remove("category_code");
            values["category"] = string.Join(",", openCategories);
            queryString = new QueryCollection(values);
        }
        var context = WithDefaultCategoryAndSeason(
            await _domainQueries.ParseMetricsContextAsync(queryString, _validator, cancellationToken));
        var finalContext = context with
        {
            SessionType = "FINAL",
            SessionTypes = ["FINAL"]
        };
        // Home keeps its lightweight final-only query. Round Analytics loads only final + its last pre-final.
        var includeAnalytics = !string.IsNullOrWhiteSpace(context.RoundId);
        var loadContext = includeAnalytics ? context with { SessionType = null, SessionTypes = [] } : finalContext;
        var matchingSessions = await _domainQueries.GetMatchingSessionsAsync(
            loadContext,
            cancellationToken: cancellationToken);
        var sessions = matchingSessions.Where(session =>
            PaceService.ParseSessionType(session.String("session_type"), session.String("name")) == PaceSessionType.Final).ToArray();
        if (sessions.Length == 0)
        {
            return EmptyRoundProfile(finalContext);
        }

        var loadSessions = includeAnalytics
            ? sessions.Concat(sessions.Select(final => RoundRaceAnalytics.SelectPreFinal(matchingSessions, final)).OfType<JsonObject>())
                .DistinctBy(session => session.String("id")).ToArray()
            : sessions;
        var detail = await _domainQueries.GetRankingPreaggregatesAsync(
            loadSessions,
            cancellationToken: cancellationToken);
        RankingDataBundle data;
        IReadOnlyList<PacePrecomputedSessionData> paceSessions;
        if (detail is not null)
        {
            data = detail.Data;
            paceSessions = detail.PaceSessions;
        }
        else
        {
            data = await _domainQueries.GetRankingDataAsync(
                loadContext,
                resolvedSessionIds: loadSessions.Select(session => session.String("id")!),
                cancellationToken: cancellationToken);
            paceSessions = [];
        }

        var resultSessionIds = data.FieldResults
            .Select(result => result.String("session_id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedSession = sessions
            .Where(session => resultSessionIds.Contains(session.String("id") ?? string.Empty))
            .OrderByDescending(SessionDate)
            .ThenByDescending(session => PublicDtoMapping.Long(session.String("id")))
            .FirstOrDefault();
        if (selectedSession is null)
        {
            return EmptyRoundProfile(finalContext);
        }

        var sessionId = selectedSession.String("id")!;
        var selectedFinals = isOpen ? sessions.Where(session =>
            SessionRound(session)?.String("id") == SessionRound(selectedSession)?.String("id")).ToArray() : [selectedSession];
        var selectedIds = selectedFinals.Select(session => session.String("id")).ToHashSet();
        var results = data.FieldResults.Where(result => selectedIds.Contains(result.String("session_id"))).ToArray();
        if (isOpen) results = RoundResultSelection.Select(results, bestOnly: false);
        var classified = results
            .Where(IsClassifiedFinish)
            .OrderBy(result => result.Int("position"))
            .ThenBy(result => result.Int("race_time_ms"))
            .ToArray();
        var leaderTime = classified
            .Select(result => result.Int("race_time_ms"))
            .Where(value => value is > 0)
            .Select(value => value!.Value)
            .DefaultIfEmpty()
            .Min();
        var round = SessionRound(selectedSession);
        var category = isOpen ? requestedCategory! : PublicApiCodes.ToPublicCategory(
            finalContext.CategoryCodes.FirstOrDefault() ?? finalContext.CategoryCode) ?? "elite_men";
        RoundRaceAnalyticsDto? analytics = null;
        if (includeAnalytics)
        {
            var preFinals = selectedFinals.Select(final => RoundRaceAnalytics.SelectPreFinal(matchingSessions, final)).OfType<JsonObject>().ToArray();
            var preFinalIds = preFinals.Select(session => session.String("id")).ToHashSet();
            // Compact field rows omit rider identity; reuse the detailed rows already in the same package.
            var detailedResults = data.Results.ToDictionary(result => result.String("id")!);
            var analyticResults = results.Select(result => detailedResults.GetValueOrDefault(result.String("id")!, result)).ToArray();
            if (isOpen) analyticResults = RoundResultSelection.Select(analyticResults, bestOnly: false);
            var finalIds = results.Select(result => result.String("id")).ToHashSet();
            var finalSplits = data.Splits.Where(split => finalIds.Contains(split.String("result_id"))).ToArray();
            var finalData = data with { Sessions = selectedFinals, Results = results, FieldResults = results, Splits = finalSplits };
            var observations = paceSessions.Count > 0
                ? _pace.CalculateSessionPaceObservations(paceSessions.Where(session => selectedIds.Contains(session.SessionId)).ToArray())
                : _pace.CalculateSessionPaceObservations(finalData);
            var previousResults = data.FieldResults.Where(result => preFinalIds.Contains(result.String("session_id"))).ToArray();
            if (isOpen) previousResults = RoundResultSelection.Select(previousResults, bestOnly: false);
            analytics = RoundRaceAnalytics.Calculate(analyticResults, finalSplits, observations, previousResults,
                preFinals.Length == 0 ? null : string.Join(" / ", preFinals.Select(RoundRaceAnalytics.PreFinalLabel).Distinct()));
        }

        return new RoundProfileResponseDto(
            round?.Int("season_year") ?? finalContext.SeasonYear ?? _timeProvider.GetUtcNow().Year,
            category,
            round is null ? null : SeasonSummaryService.ToRoundDto(round, _timeProvider.GetUtcNow()),
            PublicDtoMapping.NullableLong(sessionId),
            classified.Select(result => ToRoundResult(result, leaderTime)).ToArray(),
            BuildFieldSpread(classified, leaderTime),
            BuildTrackFingerprint(paceSessions, data, selectedFinals) with
            {
                PositionSpread = BuildFieldProgression(classified, leaderTime),
                PositionSpreadFullPosition = classified.Select(row => row.Int("position") ?? 0).DefaultIfEmpty().Max()
            }) { Analytics = analytics };
    }

    public async Task<PressurePerformanceResponseDto> GetPressurePerformanceAsync(
        string riderId,
        IQueryCollection queryString,
        CancellationToken cancellationToken)
    {
        var context = WithDefaultCategoryAndSeason(
            await _domainQueries.ParseMetricsContextAsync(queryString, _validator, cancellationToken),
            defaultSeason: false);
        var sessions = await _domainQueries.GetMatchingSessionsAsync(
            context,
            cancellationToken: cancellationToken);
        if (sessions.Count == 0)
        {
            return EmptyPressure(riderId);
        }

        // Pressure Performance is rider-specific. The detail RPC guarantees that
        // the selected rider's pace observations are present even when a compact
        // ranking package contains only partial or stale pace payloads.
        var detail = await _domainQueries.GetRiderDetailDataAsync(
            context,
            riderId,
            sessions.Select(session => session.String("id")!),
            cancellationToken: cancellationToken);
        return BuildPressurePerformance(riderId, sessions, detail.PaceSessions);
    }

    internal PressurePerformanceResponseDto BuildPressurePerformance(
        string riderId, RiderMetricsContext context, RankingSnapshot snapshot)
    {
        // Keep the standalone endpoint's default category, including unfiltered overview links.
        var bundle = DomainQueryService.RestrictToCategories(new RiderDetailDataBundle(
            new RankingDataBundle(snapshot.Sessions, [], [], [], new Dictionary<string, decimal>()),
            snapshot.PrecomputedSessions ?? []), WithDefaultCategoryAndSeason(context, defaultSeason: false));
        return BuildPressurePerformance(riderId, bundle.Data.Sessions, bundle.PaceSessions);
    }

    private PressurePerformanceResponseDto BuildPressurePerformance(
        string riderId,
        IReadOnlyList<JsonObject> sessions,
        IReadOnlyList<PacePrecomputedSessionData> paceSessions)
    {
        var observations = _pace.CalculateSessionPaceObservations(paceSessions, riderId);
        var sessionsById = sessions
            .Where(session => !string.IsNullOrWhiteSpace(session.String("id")))
            .ToDictionary(session => session.String("id")!, StringComparer.OrdinalIgnoreCase);

        var points = observations
            .Where(observation => sessionsById.ContainsKey(observation.SessionId))
            .GroupBy(observation =>
                sessionsById[observation.SessionId].String("round_id") ?? string.Empty,
                StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group => BuildPressurePoint(group.Key, group, sessionsById))
            .OrderBy(point => point.Date)
            .ThenBy(point => point.RoundId)
            .ToArray();
        var paired = points
            .Where(point => point.PreFinalPace.HasValue && point.FinalPace.HasValue)
            .ToArray();
        var qualifying = Average(paired.Select(point => point.PreFinalPace));
        var final = Average(paired.Select(point => point.FinalPace));
        decimal? delta = final.HasValue && qualifying.HasValue
            ? Math.Round(final.Value - qualifying.Value, 2)
            : null;

        var trend = delta switch
        {
            > 1m => "improves",
            < -1m => "drops",
            _ => "stable"
        };
        var evidence = paired.Length switch
        {
            >= 12 => "high",
            >= 5 => "medium",
            > 0 => "low",
            _ => "unavailable"
        };

        return new PressurePerformanceResponseDto(
            PublicDtoMapping.Long(riderId),
            delta,
            trend,
            paired.Length,
            evidence,
            new PressurePerformanceSummaryDto(
                qualifying,
                final,
                delta,
                trend,
                paired.Length),
            points);
    }

    private RiderMetricsContext WithDefaultCategoryAndSeason(
        RiderMetricsContext context,
        bool defaultSeason = true)
    {
        var category = context.CategoryCodes.FirstOrDefault() ?? context.CategoryCode ?? "EM";
        IReadOnlyList<int> seasons = context.SeasonYears;
        if (defaultSeason && seasons.Count == 0 && context.SeasonYear is null)
        {
            seasons = [_timeProvider.GetUtcNow().Year];
        }
        else if (seasons.Count == 0 && context.SeasonYear is { } seasonYear)
        {
            seasons = [seasonYear];
        }

        return context with
        {
            SeasonYear = seasons.Count == 1 ? seasons[0] : null,
            CategoryCode = category,
            SeasonYears = seasons,
            CategoryCodes = context.CategoryCodes.Count > 0 ? context.CategoryCodes : [category]
        };
    }

    private static RoundProfileResponseDto EmptyRoundProfile(RiderMetricsContext context) =>
        new(
            context.SeasonYear ?? 0,
            PublicApiCodes.ToPublicCategory(context.CategoryCode) ?? "elite_men",
            null,
            null,
            [],
            new FieldSpreadDto(0, "unavailable", null, []),
            new TrackFingerprintDto(null, []));

    private static PressurePerformanceResponseDto EmptyPressure(string riderId) =>
        new(
            PublicDtoMapping.Long(riderId),
            null,
            "unavailable",
            0,
            "unavailable",
            new PressurePerformanceSummaryDto(null, null, null, "unavailable", 0),
            []);

    private static RoundProfileResultDto ToRoundResult(JsonObject result, int leaderTime)
    {
        var rider = result.Object("riders") ?? new JsonObject();
        var team = result.Object("teams");
        var time = result.Int("race_time_ms");
        return new RoundProfileResultDto(
            result.Int("position") ?? 0,
            PublicDtoMapping.Long(result.String("id")),
            PublicDtoMapping.Long(result.String("rider_id") ?? rider.String("id")),
            rider.DisplayName(),
            rider.String("nation"),
            PublicDtoMapping.NullableLong(team?.String("id")),
            team?.String("name"),
            PublicDtoMapping.Seconds(time),
            time is > 0 && leaderTime > 0
                ? PublicDtoMapping.Seconds(time.Value - leaderTime)
                : null,
            result.Decimal("top_speed"));
    }

    internal static FieldSpreadDto BuildFieldSpread(
        IReadOnlyList<JsonObject> classified,
        int leaderTime,
        int[]? thresholds = null)
    {
        // P3 anchors the podium; the remaining cutoffs scale with classified finishers.
        var positions = thresholds ?? new[] { 3 }
            .Concat(new[] { 1m / 6, 1m / 3, 2m / 3 }
                .Select(fraction => (int)Math.Round(classified.Count * fraction, MidpointRounding.AwayFromZero))
                .Where(position => position > 3))
            .Where(position => position <= classified.Count)
            .Distinct().Order().ToArray();
        // Competition ranks skip places after ties; a cutoff inside a tie uses that tied time.
        var rankedTimes = classified.GroupBy(row => row.Int("position") ?? 0)
            .Where(group => group.Key > 0)
            .SelectMany(group => Enumerable.Range(group.Key, group.Count())
                .Select(position => (Position: position, Time: group.First().Int("race_time_ms"))))
            .ToDictionary(item => item.Position, item => item.Time);
        var points = positions
            .Where(position => rankedTimes.GetValueOrDefault(position) is > 0)
            .Select(position =>
            {
                var time = rankedTimes[position]!.Value;
                var gap = Math.Max(0, time - leaderTime);
                var thresholdIndex = Array.IndexOf(positions, position);
                var previousPosition = thresholdIndex == 0 ? 1 : positions[thresholdIndex - 1];
                var previousTime = previousPosition == 1
                    ? leaderTime
                    : rankedTimes.GetValueOrDefault(previousPosition);
                var incrementalMs = previousTime is > 0 ? (int?)Math.Max(0, time - previousTime.Value) : null;
                return new FieldSpreadPointDto(
                    position,
                    Math.Round(time / 1000m, 3),
                    Math.Round(gap / 1000m, 3),
                    leaderTime > 0 ? Math.Round(gap * 100m / leaderTime, 2) : 0m)
                {
                    PreviousPosition = previousPosition,
                    IncrementalSpread = incrementalMs.HasValue ? Math.Round(incrementalMs.Value / 1000m, 3) : null,
                    IncrementalSpreadPercent = incrementalMs.HasValue && leaderTime > 0
                        ? Math.Round(incrementalMs.Value * 100m / leaderTime, 2) : null
                };
            })
            .ToArray();
        // Classification is an existing P10 metric, independent of chart cutoffs.
        var p10Time = rankedTimes.GetValueOrDefault(10);
        decimal? p10 = p10Time is > 0 && leaderTime > 0
            ? Math.Round(Math.Max(0, p10Time.Value - leaderTime) * 100m / leaderTime, 2)
            : null;
        var classification = p10 switch
        {
            null => "unavailable",
            <= 2m => "tight",
            <= 4m => "balanced",
            _ => "spread"
        };
        return new FieldSpreadDto(classified.Count, classification, p10, points);
    }

    internal static IReadOnlyList<FieldSpreadPointDto> BuildFieldProgression(
        IReadOnlyList<JsonObject> classified, int leaderTime)
    {
        var lastPosition = classified.Select(row => row.Int("position") ?? 0).DefaultIfEmpty().Max();
        if (lastPosition < 3) return [];
        var thresholds = new[] { 3 }
            .Concat(Enumerable.Range(1, lastPosition / 5).Select(index => index * 5))
            .Append(lastPosition)
            .Distinct().Order().ToArray();
        var points = BuildFieldSpread(classified, leaderTime, thresholds).Points;
        // Compare seconds per position so the final partial block has the same scale.
        var rates = points.Select(point => point.IncrementalSpread is { } spread
            ? (double?)(spread / (point.Position - point.PreviousPosition)) : null).ToArray();
        for (var index = 3; index < points.Count; index++)
        {
            if (points[index].PreviousPosition < lastPosition / 2d) continue;
            var baseline = rates.Take(index).OfType<double>().ToArray();
            if (baseline.Length < 3) continue;
            var median = Percentile(baseline, 0.5);
            var deviation = Percentile(baseline.Select(rate => Math.Abs(rate - median)), 0.5);
            var limit = Math.Max(0.001, Math.Max(4 * median, median + 6 * deviation));
            // Never remove an interior jump followed by normal racing, or missing evidence.
            if (rates.Skip(index).All(rate => rate.HasValue && rate.Value > limit))
                return points.Take(index).ToArray();
        }
        return points;
    }

    private TrackFingerprintDto BuildTrackFingerprint(
        IReadOnlyList<PacePrecomputedSessionData> paceSessions,
        RankingDataBundle data,
        IReadOnlyList<JsonObject> sessions)
    {
        var categories = sessions.ToDictionary(session => session.String("id")!,
            session => session.Object("categories")?.String("code") ?? session.String("category_id"));
        var observations = paceSessions.Count > 0
            ? _pace.CalculateSectorObservations(
                paceSessions.Where(item => categories.ContainsKey(item.SessionId)).ToArray(),
                categories)
            : _pace.CalculateSectorObservations(data, categories)
                .Where(item => categories.ContainsKey(item.SessionId))
                .ToArray();
        var spreads = observations
            .GroupBy(item => item.SectorIndex)
            .Select(group => new
            {
                SectorIndex = group.Key,
                Count = group.Select(item => item.RiderId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                Spread = Percentile(group.Select(item => item.Score), 0.9) -
                         Percentile(group.Select(item => item.Score), 0.1)
            })
            .Where(item => item.Count >= 3)
            .OrderBy(item => item.SectorIndex)
            .ToArray();
        var maximum = spreads.Select(item => item.Spread).DefaultIfEmpty().Max();
        var total = spreads.Sum(item => Math.Max(0, item.Spread));
        var sectors = spreads.Select(item => new TrackFingerprintSectorDto(
            item.SectorIndex + 1,
            $"S{item.SectorIndex + 1}",
            item.Count,
            Math.Round((decimal)item.Spread, 2),
            maximum > 0 ? Math.Round((decimal)(item.Spread / maximum * 100), 1) : 0m,
            total > 0 ? Math.Round((decimal)(item.Spread / total * 100), 1) : 0m)).ToArray();
        return new TrackFingerprintDto(
            sectors.OrderByDescending(item => item.ScoreSpread).FirstOrDefault()?.SectorIndex,
            sectors);
    }

    private PressurePerformancePointDto BuildPressurePoint(
        string roundId,
        IEnumerable<RiderSessionPaceObservation> observations,
        Dictionary<string, JsonObject> sessionsById)
    {
        var values = observations
            .OrderBy(item => SessionDate(sessionsById[item.SessionId]))
            .ThenBy(item => sessionsById[item.SessionId].Int("session_order") ?? 0)
            .ThenBy(item => PublicDtoMapping.Long(item.SessionId))
            .ToArray();
        var finalObservation = values
            .LastOrDefault(item => item.Result.SessionType == PaceSessionType.Final);
        var preFinalObservation = finalObservation is null
            ? null
            : values
                .TakeWhile(item => !ReferenceEquals(item, finalObservation))
                .LastOrDefault(item => item.Result.SessionType != PaceSessionType.Final);
        var qualifying = preFinalObservation?.Result.Pace;
        var final = finalObservation?.Result.Pace;
        var sampleSession = finalObservation is null
            ? values.Last()
            : finalObservation;
        var sampleSessionData = sessionsById[sampleSession.SessionId];
        var round = SessionRound(sampleSessionData);
        decimal? qualifyingValue = qualifying.HasValue
            ? Math.Round((decimal)qualifying.Value, 2)
            : null;
        decimal? finalValue = final.HasValue
            ? Math.Round((decimal)final.Value, 2)
            : null;
        return new PressurePerformancePointDto(
            PublicDtoMapping.Long(roundId),
            round?.Int("season_year"),
            round?.Int("round_number"),
            PublicDtoMapping.Date(sampleSessionData.String("session_date") ?? round?.String("start_date")),
            round?.Object("tracks")?.String("venue") ?? round?.Object("tracks")?.String("name"),
            round?.Object("tracks")?.String("name") ?? round?.Object("tracks")?.String("venue"),
            preFinalObservation is null ? null : PressureSessionLabel(preFinalObservation.Result.SessionType),
            qualifyingValue,
            qualifyingValue,
            finalValue,
            qualifyingValue.HasValue && finalValue.HasValue
                ? Math.Round(finalValue.Value - qualifyingValue.Value, 2)
                : null);
    }

    private static string PressureSessionLabel(PaceSessionType sessionType) => sessionType switch
    {
        PaceSessionType.SemiFinal => "Semi-final",
        PaceSessionType.Qualifying1 => "Q1",
        PaceSessionType.Qualifying2 => "Q2",
        PaceSessionType.Qualifying => "Qualifying",
        PaceSessionType.Seeding => "Seeding",
        _ => sessionType.ToString()
    };

    private static decimal? Average(IEnumerable<decimal?> values)
    {
        var present = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return present.Length == 0 ? null : Math.Round(present.Average(), 2);
    }

    private static JsonObject? SessionRound(JsonObject session) =>
        session.Object("rounds") ?? session.Object("round_categories")?.Object("rounds");

    private static DateOnly SessionDate(JsonObject session) =>
        PublicDtoMapping.Date(
            session.String("session_date") ?? SessionRound(session)?.String("start_date")) ?? DateOnly.MinValue;

    private static bool IsClassifiedFinish(JsonObject result) =>
        (result.Bool("is_classified") == true || result.Int("position") is > 0) &&
        result.Int("position") is > 0 &&
        result.Int("race_time_ms") is > 0 &&
        !string.Equals(result.String("status"), "DNS", StringComparison.OrdinalIgnoreCase) &&
        !DownhillPaceCalculator.IsDisqualified(result.String("status"));

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        if (ordered.Length == 0)
        {
            return 0;
        }

        var index = (ordered.Length - 1) * percentile;
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        if (lower == upper)
        {
            return ordered[lower];
        }

        return ordered[lower] + (ordered[upper] - ordered[lower]) * (index - lower);
    }
}
