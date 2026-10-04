using RaceTrace.Api.Features.Metrics;
using System.Globalization;
using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Services;

public sealed class CompareService
{
    private const int MaximumComparedRiders = 2;

    private readonly QueryValidationService _validator;
    private readonly DomainQueryService _domainQueries;
    private readonly MetricsService _metrics;
    private readonly RankingService _ranking;

    public CompareService(
        QueryValidationService validator,
        DomainQueryService domainQueries,
        MetricsService metrics,
        RankingService ranking)
    {
        _validator = validator;
        _domainQueries = domainQueries;
        _metrics = metrics;
        _ranking = ranking;
    }

    public async Task<CompareResponseDto> CompareAsync(
        IQueryCollection queryString,
        CancellationToken cancellationToken)
    {
        var riderIds = ParseRiderIds(queryString, _validator);
        var context = await _domainQueries.ParseMetricsContextAsync(queryString, _validator, cancellationToken);
        if (context.CategoryCodes.Count != 1)
        {
            throw _validator.BadQuery("Select exactly one category for comparison.");
        }
        var snapshotTasks = riderIds
            .Select(riderId => _ranking.BuildSnapshotAsync(
                context,
                cancellationToken,
                riderId: riderId))
            .ToArray();
        var snapshots = await Task.WhenAll(snapshotTasks);
        var sources = new List<CompareRiderSource>(riderIds.Length);

        for (var index = 0; index < riderIds.Length; index++)
        {
            var riderId = riderIds[index];
            var snapshot = snapshots[index];
            var row = snapshot.Rows.FirstOrDefault(item => string.Equals(
                item.String("rider_id"),
                riderId,
                StringComparison.OrdinalIgnoreCase));
            var rider = row?.Object("rider")?.DeepClone().AsObject();
            if (rider is null)
            {
                rider = await _domainQueries.FindByIdAsync(
                    "riders",
                    riderId,
                    cancellationToken: cancellationToken);
                if (rider is null)
                {
                    throw new ApiException(
                        StatusCodes.Status404NotFound,
                        "RIDER_NOT_FOUND",
                        $"Rider '{riderId}' was not found.");
                }
            }

            var source = new CompareRiderSource(riderId, rider, row, snapshot);
            sources.Add(source with
            {
                RoundMetrics = await CalculateRoundMetricsAsync(source, context, cancellationToken)
            });
        }

        var response = BuildResponse(sources);
        ValidateCategoryData(response, context.CategoryCodes[0], _validator);
        return response;
    }

    internal static void ValidateCategoryData(CompareResponseDto response, string category, QueryValidationService validator)
    {
        var expected = PublicApiCodes.ToPublicCategory(category);
        bool Matches(string? value) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
        if (response.Riders.Any(rider => !rider.Categories.Any(Matches)) ||
            response.Sessions.Any(session => session.Category is not null && !Matches(session.Category)))
        {
            throw validator.BadQuery("Both riders must have data in the selected category for the selected range.");
        }
    }

    internal static string[] ParseRiderIds(
        IQueryCollection queryString,
        QueryValidationService validator)
    {
        var values = validator.CsvAny(
            queryString,
            "riderIds",
            "rider_ids",
            "riderId",
            "rider_id",
            "riders");
        var riderIds = values
            .Select(value =>
            {
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
                {
                    throw validator.BadQuery("riderIds must contain positive integer identifiers separated by commas.");
                }

                return parsed.ToString(CultureInfo.InvariantCulture);
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (riderIds.Length < 2)
        {
            throw validator.BadQuery("riderIds must contain at least two different riders.");
        }

        if (riderIds.Length > MaximumComparedRiders)
        {
            throw validator.BadQuery($"No more than {MaximumComparedRiders} riders can be compared at once.");
        }

        return riderIds;
    }

    internal static CompareResponseDto BuildResponse(IReadOnlyList<CompareRiderSource> sources)
    {
        var commonSessionIds = FindCommonSessionIds(sources)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sessionIds = sources
            .SelectMany(SourceResults)
            .Select(ResultSessionId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var sessions = sessionIds
            .Select(sessionId => BuildSession(
                sessionId,
                sources,
                commonSessionIds.Contains(sessionId)))
            .OrderByDescending(session => session.SessionDate)
            .ThenByDescending(session => session.RoundId)
            .ThenByDescending(session => session.SessionId)
            .ToArray();
        var commonSessions = sessions.Where(session => session.IsCommon).ToArray();
        var riders = sources
            .Select(source => BuildRider(source, commonSessions))
            .ToArray();

        return new CompareResponseDto(
            riders,
            sessions,
            commonSessions,
            new CompareSummaryDto(riders.Length, sessions.Length, commonSessions.Length));
    }

    internal static IReadOnlyList<string> FindCommonSessionIds(IReadOnlyList<CompareRiderSource> sources)
    {
        HashSet<string>? common = null;
        foreach (var source in sources)
        {
            var riderSessions = SourceResults(source)
                .Where(RankingService.HasStarted)
                .Select(ResultSessionId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (common is null)
            {
                common = riderSessions;
            }
            else
            {
                common.IntersectWith(riderSessions);
            }
        }

        return common?.ToArray() ?? [];
    }

    private static CompareRiderDto BuildRider(
        CompareRiderSource source,
        IReadOnlyList<CompareSessionDto> commonSessions)
    {
        var results = SourceResults(source);
        var finalPositions = results
            .Where(DomainQueryService.IsFinalResult)
            .Where(RankingService.HasStarted)
            .Select(result => result.Int("position"))
            .Where(position => position is > 0)
            .Select(position => position!.Value)
            .ToArray();
        var preFinalPositions = results
            .Where(result => !DomainQueryService.IsFinalResult(result))
            .Where(RankingService.HasStarted)
            .Select(result => result.Int("position"))
            .Where(position => position is > 0)
            .Select(position => position!.Value)
            .ToArray();
        var resultIds = results
            .Select(result => result.String("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wonSplits = CountTimingWins(source.Snapshot.Splits, resultIds, "split_rank");
        var wonSectors = source.Snapshot.PrecomputedSessions is not null
            ? source.Snapshot.PrecomputedSessions
                .Where(session => string.Equals(
                    session.Run.RiderId,
                    source.RiderId,
                    StringComparison.OrdinalIgnoreCase))
                .SelectMany(session => session.Sectors)
                .Count(sector => sector.Rank == 1)
            : CountTimingWins(source.Snapshot.Splits, resultIds, "sector_rank");
        var team = source.Rider.Array("teams")?.OfType<JsonObject>().FirstOrDefault() ??
            results
                .OrderByDescending(ResultDate)
                .Select(result => result.Object("teams"))
                .FirstOrDefault(item => item is not null);
        var finishGaps = commonSessions
            .SelectMany(session => session.Results)
            .Where(result => result.RiderId == PublicDtoMapping.Long(source.RiderId))
            .Select(result => result.GapToBestSelected)
            .Where(gap => gap.HasValue)
            .Select(gap => gap!.Value)
            .ToArray();
        var sectorGaps = commonSessions
            .SelectMany(session => session.Results
                .Where(result => result.RiderId == PublicDtoMapping.Long(source.RiderId))
                .SelectMany(result => result.Sectors
                    .Where(sector => sector.GapToBestSelected.HasValue)
                    .Select(sector => new
                    {
                        session.TrackId,
                        session.TrackName,
                        Sector = sector
                    })))
            .GroupBy(item => new
            {
                item.TrackId,
                item.TrackName,
                item.Sector.SectorIndex,
                item.Sector.IsFinishSector
            })
            .OrderBy(group => group.Key.TrackName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key.TrackId)
            .ThenBy(group => group.Key.SectorIndex)
            .Select(group => new CompareAverageSectorGapDto(
                group.Key.TrackId,
                group.Key.TrackName,
                group.Key.SectorIndex,
                group.Key.IsFinishSector,
                Round(group.Average(item => item.Sector.GapToBestSelected!.Value)),
                group.Count()))
            .ToArray();
        var row = source.RankingRow;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var categories = results
            .Select(result => PublicApiCodes.ToPublicCategory(
                result.Object("sessions")?.Object("categories")?.String("code")))
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Select(category => category!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(category => category, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var pointsProgress = BuildPointsProgress(results);
        var roundMetrics = BuildRoundMetrics(source, results);

        return new CompareRiderDto(
            PublicDtoMapping.Long(source.RiderId),
            source.Rider.String("uci_id"),
            source.Rider.String("first_name"),
            source.Rider.String("last_name"),
            source.Rider.DisplayName(),
            source.Rider.String("nation"),
            source.Rider.String("profile_image_url"),
            RiderCategoryResolver.CalculateAge(
                PublicDtoMapping.Date(source.Rider.String("birth_date")),
                today),
            RiderCategoryResolver.CurrentCategory(source.Rider, today),
            categories,
            PublicDtoMapping.NullableLong(team?.String("id")),
            team?.String("name"),
            team?.String("code"),
            finalPositions.Count(position => position == 1),
            finalPositions.Count(position => position <= 3),
            finalPositions.Count(position => position <= 5),
            finalPositions.Count(position => position <= 10),
            row?.Decimal("rating"),
            row?.Decimal("pace"),
            row?.Decimal("consistency"),
            row?.Decimal("confidence") ?? 0m,
            wonSplits,
            wonSectors,
            Average(finalPositions),
            Average(preFinalPositions),
            finishGaps.Length == 0 ? null : Round(finishGaps.Average()),
            finishGaps.Length,
            sectorGaps,
            pointsProgress.LastOrDefault()?.CumulativePoints ?? 0m,
            pointsProgress,
            roundMetrics)
        {
            ConsistencyEvidence = row?.Decimal("consistency_confidence") ?? 0m,
            SectorGapSummary = SummarizeSectorGaps(sectorGaps),
            ResultsAveragePosition = commonSessions.SelectMany(session => session.Results)
                .Where(result => result.RiderId == PublicDtoMapping.Long(source.RiderId) && result.IsClassified && result.Position > 0)
                .Select(result => (decimal?)result.Position).Average()
        };
    }

    internal static IReadOnlyList<CompareSectorGapSummaryDto> SummarizeSectorGaps(IEnumerable<CompareAverageSectorGapDto> sectors) =>
        sectors.Where(sector => !sector.IsFinishSector)
            .GroupBy(sector => sector.SectorIndex)
            .OrderBy(group => group.Key)
            .Select(group => new CompareSectorGapSummaryDto(
                group.Key,
                group.Sum(sector => sector.SamplesCount) == 0 ? null :
                    group.Sum(sector => sector.AverageGap * sector.SamplesCount) / group.Sum(sector => sector.SamplesCount),
                group.Sum(sector => sector.SamplesCount)))
            .ToArray();

    private static CompareSessionDto BuildSession(
        string sessionId,
        IReadOnlyList<CompareRiderSource> sources,
        bool isCommon)
    {
        var sourceResults = sources
            .Select(source => new
            {
                Source = source,
                Result = SourceResults(source)
                    .Where(result => string.Equals(ResultSessionId(result), sessionId, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(result => PublicDtoMapping.Long(result.String("id")))
                    .FirstOrDefault()
            })
            .Where(item => item.Result is not null)
            .Select(item => new { item.Source, Result = item.Result! })
            .ToArray();
        var session = sourceResults
            .Select(item => item.Result.Object("sessions"))
            .First(item => item is not null)!;
        var round = session.Object("rounds") ?? new JsonObject();
        var track = round.Object("tracks");
        var competitionClass = round.Object("competition_class") ?? round.Object("competition_levels");
        var validFinishTimes = sourceResults
            .Where(item => IsClassified(item.Result))
            .Select(item => item.Result.Int("race_time_ms"))
            .Where(time => time is > 0)
            .Select(time => time!.Value)
            .ToArray();
        var bestFinishTime = validFinishTimes.Length == 0 ? (int?)null : validFinishTimes.Min();
        var precomputedByRider = sourceResults.ToDictionary(
            item => item.Source.RiderId,
            item => item.Source.Snapshot.PrecomputedSessions?
                .Where(precomputed => string.Equals(precomputed.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(precomputed => precomputed.Sectors.Count)
                .FirstOrDefault(),
            StringComparer.OrdinalIgnoreCase);
        var bestSectorTimes = precomputedByRider.Values
            .Where(precomputed => precomputed is not null)
            .SelectMany(precomputed => precomputed!.Sectors)
            .Where(sector => IsValidTime(sector.TimeSeconds))
            .GroupBy(sector => sector.SectorIndex)
            .Where(group => !isCommon || group.Count() == sources.Count)
            .ToDictionary(group => group.Key, group => group.Min(sector => sector.TimeSeconds!.Value));

        var results = sourceResults
            .Select(item =>
            {
                var result = item.Result;
                var resultId = result.String("id") ?? string.Empty;
                var finishTimeMs = IsClassified(result) && result.Int("race_time_ms") is > 0
                    ? result.Int("race_time_ms")
                    : null;
                var precomputed = precomputedByRider.GetValueOrDefault(item.Source.RiderId);
                var splitCount = item.Source.Snapshot.Splits.Count(split => string.Equals(
                    split.String("result_id"),
                    resultId,
                    StringComparison.OrdinalIgnoreCase));
                var sectors = precomputed?.Sectors
                    .OrderBy(sector => sector.SectorIndex)
                    .Select(sector =>
                    {
                        var time = IsValidTime(sector.TimeSeconds)
                            ? Round((decimal)sector.TimeSeconds!.Value)
                            : (decimal?)null;
                        var best = bestSectorTimes.GetValueOrDefault(sector.SectorIndex);
                        var gap = time.HasValue && best > 0 && sector.TimeSeconds.HasValue
                            ? Round((decimal)(sector.TimeSeconds.Value - best))
                            : (decimal?)null;
                        var selectedTimes = precomputedByRider.Values
                            .Where(candidate => candidate is not null)
                            .SelectMany(candidate => candidate!.Sectors)
                            .Where(candidate => candidate.SectorIndex == sector.SectorIndex && IsValidTime(candidate.TimeSeconds))
                            .Select(candidate => candidate.TimeSeconds!.Value)
                            .ToArray();
                        var selectedRank = IsValidTime(sector.TimeSeconds)
                            ? 1 + selectedTimes.Count(candidate => candidate < sector.TimeSeconds!.Value)
                            : (int?)null;
                        var isFinishSector = IsValidTime(precomputed?.Run.FinishTimeSeconds) &&
                            sector.SectorIndex >= splitCount;
                        return new CompareSectorResultDto(
                            sector.SectorIndex + 1,
                            isFinishSector,
                            time,
                            gap,
                            selectedRank);
                    })
                    .ToArray() ?? [];
                var team = result.Object("teams");

                return new CompareSessionResultDto(
                    PublicDtoMapping.Long(resultId),
                    PublicDtoMapping.Long(item.Source.RiderId),
                    result.Int("position"),
                    result.String("status"),
                    IsClassified(result),
                    PublicDtoMapping.NullableLong(team?.String("id")),
                    team?.String("name"),
                    PublicDtoMapping.Seconds(finishTimeMs),
                    finishTimeMs.HasValue && bestFinishTime.HasValue
                        ? PublicDtoMapping.Seconds(finishTimeMs.Value - bestFinishTime.Value)
                        : null,
                    sectors);
            })
            .ToArray();

        return new CompareSessionDto(
            PublicDtoMapping.Long(sessionId),
            PublicDtoMapping.Long(session.String("round_id") ?? round.String("id")),
            round.Int("season_year"),
            round.Int("round_number"),
            session.String("name"),
            PublicApiCodes.ToPublicSessionType(session.String("session_type")),
            PublicApiCodes.ToPublicCategory(session.Object("categories")?.String("code")),
            PublicDtoMapping.Date(session.String("session_date") ?? round.String("start_date")),
            PublicDtoMapping.NullableLong(round.String("track_id") ?? track?.String("id")),
            track?.String("name"),
            track?.String("venue"),
            track?.String("country_code"),
             PublicDtoMapping.Condition(session.Object("conditions") ?? round.Object("conditions")),
            PublicDtoMapping.NullableLong(round.String("competition_class_id") ?? round.String("competition_level_id") ?? competitionClass?.String("id")),
            competitionClass?.String("code"),
            competitionClass?.String("name"),
            isCommon,
            results);
    }

    private async Task<IReadOnlyDictionary<string, RiderPerformanceMetricValues>> CalculateRoundMetricsAsync(
        CompareRiderSource source,
        RiderMetricsContext context,
        CancellationToken cancellationToken)
    {
        var metrics = new Dictionary<string, RiderPerformanceMetricValues>(StringComparer.OrdinalIgnoreCase);
        var groups = SourceResults(source)
            .GroupBy(ResultRoundId, StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key));
        foreach (var group in groups)
        {
            var roundResults = group.ToArray();
            var sessionIds = roundResults
                .Select(ResultSessionId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var availableSessions = source.Snapshot.Sessions
                .Where(session => sessionIds.Contains(session.String("id") ?? string.Empty))
                .ToArray();
            IReadOnlyDictionary<string, RiderPerformanceMetricValues> calculated;
            if (source.Snapshot.PrecomputedSessions is not null)
            {
                var precomputed = source.Snapshot.PrecomputedSessions
                    .Where(session => sessionIds.Contains(session.SessionId))
                    .ToArray();
                calculated = _metrics.CalculatePrecomputedPerformanceByRider(
                    roundResults,
                    precomputed,
                    availableSessions);
            }
            else
            {
                var fieldResults = source.Snapshot.FieldResults
                    .Where(result => sessionIds.Contains(ResultSessionId(result) ?? string.Empty))
                    .ToArray();
                var fieldResultIds = fieldResults
                    .Select(result => result.String("id"))
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                calculated = await _metrics.CalculatePerformanceByRiderAsync(
                    roundResults,
                    context,
                    cancellationToken,
                    availableSessions: availableSessions,
                    suppliedSplits: source.Snapshot.Splits
                        .Where(split => fieldResultIds.Contains(split.String("result_id") ?? string.Empty))
                        .ToArray(),
                    suppliedFieldResults: fieldResults);
            }

            if (calculated.TryGetValue(source.RiderId, out var performance))
            {
                metrics[group.Key] = performance;
            }
        }

        return metrics;
    }

    private static IReadOnlyList<ComparePointsProgressDto> BuildPointsProgress(
        IReadOnlyList<JsonObject> results)
    {
        decimal cumulative = 0m;
        return results
            .OrderBy(ResultDate, StringComparer.Ordinal)
            .ThenBy(result => PublicDtoMapping.Long(ResultSessionId(result)))
            .ThenBy(result => PublicDtoMapping.Long(result.String("id")))
            .Select(result =>
            {
                var session = result.Object("sessions");
                var round = session?.Object("rounds");
                var awarded = result.Decimal("awarded_points") ?? 0m;
                cumulative += awarded;
                return new ComparePointsProgressDto(
                    PublicDtoMapping.Long(result.String("id")),
                    PublicDtoMapping.Long(ResultSessionId(result)),
                    PublicDtoMapping.Long(ResultRoundId(result)),
                    PublicDtoMapping.Date(session?.String("session_date") ?? round?.String("start_date")),
                    PublicApiCodes.ToPublicSessionType(session?.String("session_type")),
                    PublicApiCodes.ToPublicCategory(session?.Object("categories")?.String("code")),
                    awarded,
                    cumulative);
            })
            .ToArray();
    }

    private static IReadOnlyList<CompareRoundMetricsDto> BuildRoundMetrics(
        CompareRiderSource source,
        IReadOnlyList<JsonObject> results)
    {
        return results
            .GroupBy(ResultRoundId, StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group =>
            {
                var result = group.OrderBy(ResultDate, StringComparer.Ordinal).First();
                var session = result.Object("sessions");
                var round = session?.Object("rounds");
                var track = round?.Object("tracks");
                var competitionClass = round?.Object("competition_levels") ?? round?.Object("competition_class");
                RiderPerformanceMetricValues? performance = null;
                if (source.RoundMetrics is not null)
                {
                    source.RoundMetrics.TryGetValue(group.Key, out performance);
                }
                return new CompareRoundMetricsDto(
                    PublicDtoMapping.Long(group.Key),
                    round?.Int("season_year"),
                    round?.Int("round_number"),
                    PublicDtoMapping.Date(round?.String("start_date") ?? session?.String("session_date")),
                    PublicDtoMapping.NullableLong(round?.String("track_id") ?? track?.String("id")),
                    track?.String("name"),
                    track?.String("venue"),
                    competitionClass?.String("code"),
                    performance?.Rating,
                    performance?.Pace,
                    performance?.Consistency,
                    performance?.PaceConfidence ?? 0m)
                {
                    ConsistencyEvidence = performance?.ConsistencyEvidence ?? 0m
                };
            })
            .OrderBy(item => item.Date)
            .ThenBy(item => item.RoundId)
            .ToArray();
    }

    private static IReadOnlyList<JsonObject> SourceResults(CompareRiderSource source) =>
        source.Snapshot.Results
            .Where(result => string.Equals(result.String("rider_id"), source.RiderId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private static int CountTimingWins(
        IReadOnlyList<JsonObject> splits,
        IReadOnlySet<string> resultIds,
        string rankField)
    {
        return splits
            .Where(split => resultIds.Contains(split.String("result_id") ?? string.Empty) && split.Int(rankField) == 1)
            .Select(split => $"{split.String("result_id")}:{split.Int("split_index")}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    private static bool IsClassified(JsonObject result)
    {
        var status = result.String("status");
        return (result.Bool("is_classified") == true || result.Int("position") is > 0) &&
            !string.Equals(status, "DNF", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "DNS", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "DSQ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidTime(double? value) =>
        value is > 0 && double.IsFinite(value.Value);

    private static string? ResultSessionId(JsonObject result) =>
        result.String("session_id") ?? result.Object("sessions")?.String("id");

    private static string ResultRoundId(JsonObject result)
    {
        var session = result.Object("sessions");
        return session?.String("round_id") ??
            session?.Object("rounds")?.String("id") ??
            string.Empty;
    }

    private static string ResultDate(JsonObject result) =>
        result.Object("sessions")?.String("session_date") ??
        result.Object("sessions")?.Object("rounds")?.String("start_date") ??
        string.Empty;

    private static decimal? Average(IReadOnlyList<int> values) =>
        values.Count == 0
            ? null
            : Math.Round((decimal)values.Average(), 2, MidpointRounding.AwayFromZero);

    private static decimal Round(decimal value) =>
        Math.Round(value, 3, MidpointRounding.AwayFromZero);
}

internal sealed record CompareRiderSource(
    string RiderId,
    JsonObject Rider,
    JsonObject? RankingRow,
    RankingSnapshot Snapshot,
    IReadOnlyDictionary<string, RiderPerformanceMetricValues>? RoundMetrics = null);
