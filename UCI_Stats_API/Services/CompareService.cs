using System.Globalization;
using System.Text.Json.Nodes;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Models;

namespace UCI_Stats_API.Services;

public sealed class CompareService
{
    private const int MaximumComparedRiders = 10;

    private readonly QueryValidationService _validator;
    private readonly DomainQueryService _domainQueries;
    private readonly RankingService _ranking;

    public CompareService(
        QueryValidationService validator,
        DomainQueryService domainQueries,
        RankingService ranking)
    {
        _validator = validator;
        _domainQueries = domainQueries;
        _ranking = ranking;
    }

    public async Task<CompareResponseDto> CompareAsync(
        IQueryCollection queryString,
        CancellationToken cancellationToken)
    {
        var riderIds = ParseRiderIds(queryString, _validator);
        var context = DomainQueryService.MetricsContextFromQuery(queryString, _validator);
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

            sources.Add(new CompareRiderSource(riderId, rider, row, snapshot));
        }

        return BuildResponse(sources);
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
        var commonSessionIds = FindCommonSessionIds(sources);
        var commonSessions = commonSessionIds
            .Select(sessionId => BuildCommonSession(sessionId, sources))
            .OrderByDescending(session => session.SessionDate)
            .ThenByDescending(session => session.RoundId)
            .ThenByDescending(session => session.SessionId)
            .ToArray();
        var riders = sources
            .Select(source => BuildRider(source, commonSessions))
            .ToArray();

        return new CompareResponseDto(
            riders,
            commonSessions,
            new CompareSummaryDto(riders.Length, commonSessions.Length));
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
        var wonSectors = CountTimingWins(source.Snapshot.Splits, resultIds, "sector_rank");
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

        return new CompareRiderDto(
            PublicDtoMapping.Long(source.RiderId),
            source.Rider.String("uci_id"),
            source.Rider.String("first_name"),
            source.Rider.String("last_name"),
            source.Rider.DisplayName(),
            source.Rider.String("nation"),
            source.Rider.String("profile_image_url"),
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
            sectorGaps);
    }

    private static CompareSessionDto BuildCommonSession(
        string sessionId,
        IReadOnlyList<CompareRiderSource> sources)
    {
        var sourceResults = sources
            .Select(source => new
            {
                Source = source,
                Result = SourceResults(source)
                    .Where(result => string.Equals(ResultSessionId(result), sessionId, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(result => PublicDtoMapping.Long(result.String("id")))
                    .First()
            })
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
        var bestFinishTime = validFinishTimes.Length < 2 ? (int?)null : validFinishTimes.Min();
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
            .Where(group => group.Count() >= 2)
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
                            sector.SectorIndex == splitCount;
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
            PublicDtoMapping.Condition(round.Object("conditions")),
            PublicDtoMapping.NullableLong(round.String("competition_class_id") ?? round.String("competition_level_id") ?? competitionClass?.String("id")),
            competitionClass?.String("code"),
            competitionClass?.String("name"),
            results);
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
    RankingSnapshot Snapshot);
