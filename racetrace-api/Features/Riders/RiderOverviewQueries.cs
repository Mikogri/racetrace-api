using RaceTrace.Api.Features.Metrics;
using RaceTrace.Api.Features.Riders;
using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;

using RaceTrace.Api.Services;
using static RaceTrace.Api.Features.Riders.RiderStatisticsProjection;

namespace RaceTrace.Api.Features.Riders;

internal sealed class RiderOverviewQueries(
    QueryValidationService validator, DomainQueryService domainQueries,
    RankingService ranking, AnalyticsService analytics, RiderResultQueries results)
{
    private readonly QueryValidationService _validator = validator;
    private readonly DomainQueryService _domainQueries = domainQueries;
    private readonly RankingService _ranking = ranking;
    private readonly AnalyticsService _analytics = analytics;
    private readonly RiderResultQueries _results = results;

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
        int? suppliedRankingPosition = null,
        Task<IReadOnlyList<JsonObject>>? suppliedTeamHistoryTask = null)
    {
        var context = await _domainQueries.ParseMetricsContextAsync(queryString, _validator, cancellationToken);
        var currentPresentationTask = GetCurrentPresentationAsync(
            riderId,
            cancellationToken, suppliedTeamHistoryTask);
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
        var sectorRows = RiderTimingStatistics.BuildSectorRows(splits, allResults);
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
        var teamHistoryTask = _domainQueries.GetRiderTeamHistoryAsync([riderId], cancellationToken: cancellationToken);
        var snapshotTask = _ranking.BuildSnapshotAsync(
            context,
            cancellationToken,
            riderId: riderId);
        var rankingPositionTask = _ranking.GetPointsPositionAsync(
            context,
            riderId,
            cancellationToken);
        await Task.WhenAll(snapshotTask, rankingPositionTask, teamHistoryTask);
        var snapshot = await snapshotTask;
        var rankingPosition = await rankingPositionTask;
        var statisticsTask = GetPublicStatisticsCoreAsync(
            riderId,
            overviewQuery,
            cancellationToken,
            snapshot,
            rankingPosition, teamHistoryTask);
        var roundResultsTask = _results.ListPublicRiderRoundResultsCoreAsync(
            riderId,
            overviewQuery,
            cancellationToken,
            snapshot);
        var trackStatisticsTask = _results.ListPublicTrackStatisticsCoreAsync(
            riderId,
            overviewQuery,
            cancellationToken,
            snapshot);
        var pressurePerformance = _analytics.BuildPressurePerformance(
            riderId, context, snapshot);
        await Task.WhenAll(
            statisticsTask,
            roundResultsTask,
            trackStatisticsTask,
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
            JsonSerializerProxy.ToJsonObject(pressurePerformance),
            RiderTeamHistoryMapper.ToPublicTeamHistoryJson(await teamHistoryTask))
        {
            ChartStatistics = RiderChartStatistics.Build(sessionResults.OfType<JsonObject>(),
                (await roundResultsTask).Items, (await trackStatisticsTask).Items) with
            {
                CumulativeRatingByRound = await _results.CumulativeRatingsAsync(riderId, context, snapshot, cancellationToken)
            }
        };
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

    private async Task<CurrentRiderPresentation> GetCurrentPresentationAsync(
        string riderId,
        CancellationToken cancellationToken,
        Task<IReadOnlyList<JsonObject>>? suppliedTeamHistoryTask = null)
    {
        var riderTask = _domainQueries.FindByIdAsync(
            "riders",
            riderId,
            cancellationToken: cancellationToken);
        var teamHistoryTask = suppliedTeamHistoryTask ?? _domainQueries.GetRiderTeamHistoryAsync(
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

}
