using UCI_Stats_API.Features.Metrics;
using System.Text.Json.Nodes;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Models;

namespace UCI_Stats_API.Services;

public sealed class AnalyticsService
{
    private static readonly int[] SpreadPositions = [3, 5, 10, 20];

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
        var context = WithDefaultCategoryAndSeason(
            await _domainQueries.ParseMetricsContextAsync(queryString, _validator, cancellationToken));
        var finalContext = context with
        {
            SessionType = "FINAL",
            SessionTypes = ["FINAL"]
        };
        var sessions = await _domainQueries.GetMatchingSessionsAsync(
            finalContext,
            cancellationToken: cancellationToken);
        if (sessions.Count == 0)
        {
            return EmptyRoundProfile(finalContext);
        }

        var detail = await _domainQueries.GetRankingPreaggregatesAsync(
            sessions,
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
                finalContext,
                resolvedSessionIds: sessions.Select(session => session.String("id")!),
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
        var results = data.FieldResults
            .Where(result => string.Equals(
                result.String("session_id"),
                sessionId,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
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
        var category = PublicApiCodes.ToPublicCategory(
            finalContext.CategoryCodes.FirstOrDefault() ?? finalContext.CategoryCode) ?? "elite_men";

        return new RoundProfileResponseDto(
            round?.Int("season_year") ?? finalContext.SeasonYear ?? _timeProvider.GetUtcNow().Year,
            category,
            round is null ? null : SeasonSummaryService.ToRoundDto(round, _timeProvider.GetUtcNow()),
            PublicDtoMapping.NullableLong(sessionId),
            classified.Select(result => ToRoundResult(result, leaderTime)).ToArray(),
            BuildFieldSpread(classified, leaderTime),
            BuildTrackFingerprint(paceSessions, data, selectedSession));
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
        var observations = _pace.CalculateSessionPaceObservations(
            detail.PaceSessions,
            riderId);
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
            CategoryCodes = [category]
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

    private static FieldSpreadDto BuildFieldSpread(
        IReadOnlyList<JsonObject> classified,
        int leaderTime)
    {
        var points = SpreadPositions
            .Select(position => classified.FirstOrDefault(result => result.Int("position") == position))
            .Where(result => result?.Int("race_time_ms") is > 0)
            .Select(result =>
            {
                var row = result!;
                var time = row.Int("race_time_ms")!.Value;
                var position = row.Int("position") ?? 0;
                var gap = Math.Max(0, time - leaderTime);
                return new FieldSpreadPointDto(
                    position,
                    Math.Round(time / 1000m, 3),
                    Math.Round(gap / 1000m, 3),
                    leaderTime > 0 ? Math.Round(gap * 100m / leaderTime, 2) : 0m);
            })
            .ToArray();
        var p10 = points.FirstOrDefault(point => point.Position == 10)?.GapPercent;
        var classification = p10 switch
        {
            null => "unavailable",
            <= 2m => "tight",
            <= 4m => "balanced",
            _ => "spread"
        };
        return new FieldSpreadDto(classified.Count, classification, p10, points);
    }

    private TrackFingerprintDto BuildTrackFingerprint(
        IReadOnlyList<PacePrecomputedSessionData> paceSessions,
        RankingDataBundle data,
        JsonObject session)
    {
        var sessionId = session.String("id") ?? string.Empty;
        var categories = new Dictionary<string, string?>
        {
            [sessionId] = session.Object("categories")?.String("code") ?? session.String("category_id")
        };
        var observations = paceSessions.Count > 0
            ? _pace.CalculateSimilarityObservations(
                paceSessions.Where(item => string.Equals(
                    item.SessionId,
                    sessionId,
                    StringComparison.OrdinalIgnoreCase)).ToArray(),
                categories)
            : _pace.CalculateSimilarityObservations(data, categories)
                .Where(item => string.Equals(item.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
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
        IReadOnlyDictionary<string, JsonObject> sessionsById)
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
