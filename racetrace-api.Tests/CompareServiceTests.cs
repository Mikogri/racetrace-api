using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class CompareServiceTests
{
    [Fact]
    public void RiderListRequiresTwoDifferentPositiveIdentifiers()
    {
        var validator = new QueryValidationService();

        Assert.Equal(["3", "8"], CompareService.ParseRiderIds(Query("3,8,3"), validator));
        Assert.Throws<ApiException>(() => CompareService.ParseRiderIds(Query("3,8,11"), validator));
        Assert.Throws<ApiException>(() => CompareService.ParseRiderIds(Query("3,3"), validator));
        Assert.Throws<ApiException>(() => CompareService.ParseRiderIds(Query("3,nope"), validator));
    }

    [Fact]
    public void CommonSessionsContainEveryStarterIncludeDnfAndExcludeDns()
    {
        var first = Source(
            "1",
            [
                Result("11", "1", "100", "Q1", 2, "Finished", 100_000),
                Result("12", "1", "200", "FINAL", 1, "Finished", 200_000),
                Result("13", "1", "300", "FINAL", 4, "Finished", 205_000),
                Result("14", "1", "400", "Q2", 80, "DNF", null),
                Result("15", "1", "500", "Q2", null, "DNS", null),
            ],
            [],
            []
        );
        var second = Source(
            "2",
            [
                Result("21", "2", "100", "Q1", 1, "Finished", 98_000),
                Result("22", "2", "200", "FINAL", 2, "Finished", 203_000),
                Result("24", "2", "400", "Q2", 1, "Finished", 190_000),
                Result("25", "2", "500", "Q2", 1, "Finished", 188_000),
            ],
            [],
            []
        );

        var common = CompareService.FindCommonSessionIds([first, second]);

        Assert.Equal(["100", "200", "400"], common.Order().ToArray());
    }

    [Fact]
    public void BuildsFinalStatisticsAndHeadToHeadGapsWithoutDuplicatingSessions()
    {
        var firstResults = new[]
        {
            Result("11", "1", "100", "Q1", 2, "Finished", 100_000),
            Result("12", "1", "200", "FINAL", 1, "Finished", 200_000),
            Result("13", "1", "300", "FINAL", 4, "Finished", 205_000),
        };
        var secondResults = new[]
        {
            Result("21", "2", "100", "Q1", 1, "Finished", 102_000),
            Result("22", "2", "200", "FINAL", 2, "Finished", 203_000),
        };
        firstResults[0]["awarded_points"] = 10;
        firstResults[1]["awarded_points"] = 20;
        firstResults[2]["awarded_points"] = 30;
        var first = Source(
            "1",
            firstResults,
            [
                Split("11", 1, splitRank: 1, sectorRank: 1),
                Split("12", 1, splitRank: 1, sectorRank: 1),
            ],
            [PaceSession("1", "100", 100, 50, 50), PaceSession("1", "200", 200, 90, 110)],
            rating: 96,
            pace: 97,
            consistency: 92,
            confidence: 88
        );
        var second = Source(
            "2",
            secondResults,
            [
                Split("21", 1, splitRank: 2, sectorRank: 2),
                Split("22", 1, splitRank: 2, sectorRank: 2),
            ],
            [PaceSession("2", "100", 102, 51, 51), PaceSession("2", "200", 203, 92, 111)],
            rating: 93,
            pace: 94,
            consistency: 90,
            confidence: 84
        );

        var response = CompareService.BuildResponse([first, second]);

        Assert.Equal(2, response.Summary.RidersCount);
        Assert.Equal(3, response.Summary.SessionsCount);
        Assert.Equal(2, response.Summary.CommonSessionsCount);
        Assert.Equal(3, response.Sessions.Count);
        Assert.Equal(2, response.CommonSessions.Count);
        Assert.Single(response.Sessions, session => !session.IsCommon);
        Assert.All(response.CommonSessions, session => Assert.Equal(2, session.Results.Count));

        var firstRider = response.Riders[0];
        Assert.Equal(1, firstRider.WinsCount);
        Assert.Equal(1, firstRider.PodiumsCount);
        Assert.Equal(2, firstRider.Top5Count);
        Assert.Equal(2, firstRider.Top10Count);
        Assert.Equal(2.5m, firstRider.AverageFinalPosition);
        Assert.Equal(2m, firstRider.AveragePreFinalPosition);
        Assert.Equal(2, firstRider.WonSplitsCount);
        Assert.Equal(2, firstRider.WonSectorsCount);
        Assert.Equal(96m, firstRider.Rating);
        Assert.Equal(0m, firstRider.AverageFinishGapToBestSelected);
        Assert.Equal(
            [10m, 30m, 60m],
            firstRider.PointsProgress.Select(point => point.CumulativePoints).ToArray()
        );
        Assert.Equal(60m, firstRider.TotalPoints);
        Assert.Equal(["elite_men"], firstRider.Categories);
        Assert.Equal(1.5m, firstRider.ResultsAveragePosition);
        CompareService.ValidateCategoryData(response, "EM", new QueryValidationService());
        Assert.Throws<ApiException>(() =>
            CompareService.ValidateCategoryData(response, "EW", new QueryValidationService())
        );
        Assert.Throws<ApiException>(() =>
            CompareService.ValidateCategoryData(
                response with
                {
                    Riders = [firstRider with { Categories = [] }, response.Riders[1]],
                },
                "EM",
                new QueryValidationService()
            )
        );

        var secondRider = response.Riders[1];
        Assert.Equal(2.5m, secondRider.AverageFinishGapToBestSelected);
        Assert.Equal(2, secondRider.FinishGapSamplesCount);
        Assert.Equal(
            [1.5m, 1m],
            secondRider.AverageSectorGapsToBestSelected.Select(gap => gap.AverageGap).ToArray()
        );
        Assert.Equal(
            [1, 2],
            secondRider.AverageSectorGapsToBestSelected.Select(gap => gap.SectorIndex).ToArray()
        );
    }

    [Fact]
    public void SectorGapAveragesUseOnlySectorsAvailableForEveryComparedRider()
    {
        var first = Source(
            "1",
            [Result("11", "1", "100", "Q1", 1, "Finished", 100_000)],
            [],
            [PaceSession("1", "100", 100, 40, 60)]
        );
        var second = Source(
            "2",
            [Result("21", "2", "100", "Q1", 2, "DNF", null)],
            [],
            [PaceSession("2", "100", 100, 42)]
        );

        var response = CompareService.BuildResponse([first, second]);

        Assert.Single(response.CommonSessions);
        Assert.All(
            response.CommonSessions[0].Results,
            result => Assert.Single(result.Sectors, sector => sector.GapToBestSelected.HasValue)
        );
        Assert.All(response.Riders, rider => Assert.Single(rider.AverageSectorGapsToBestSelected));
        Assert.Equal(2m, response.Riders[1].AverageSectorGapsToBestSelected[0].AverageGap);
    }

    private static QueryCollection Query(string riderIds) =>
        new(new Dictionary<string, StringValues> { ["riderIds"] = riderIds });

    private static CompareRiderSource Source(
        string riderId,
        IReadOnlyList<JsonObject> results,
        IReadOnlyList<JsonObject> splits,
        IReadOnlyList<PacePrecomputedSessionData> paceSessions,
        decimal? rating = null,
        decimal? pace = null,
        decimal? consistency = null,
        decimal confidence = 0
    )
    {
        var rider = new JsonObject
        {
            ["id"] = riderId,
            ["uci_id"] = $"UCI{riderId}",
            ["first_name"] = $"Rider{riderId}",
            ["last_name"] = "Test",
            ["nation"] = "FRA",
            ["teams"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = riderId,
                    ["name"] = $"Team {riderId}",
                    ["code"] = $"T{riderId}",
                }
            ),
        };
        foreach (var result in results)
        {
            result["riders"] = rider.DeepClone();
        }

        var row = new JsonObject
        {
            ["rider_id"] = riderId,
            ["rider"] = rider.DeepClone(),
            ["rating"] = rating,
            ["pace"] = pace,
            ["consistency"] = consistency,
            ["confidence"] = confidence,
        };
        var sessions = results
            .Select(result => result.Object("sessions")!.DeepClone().AsObject())
            .GroupBy(session => session.String("id"))
            .Select(group => group.First())
            .ToArray();
        var snapshot = new RankingSnapshot([row], results, results, splits, sessions, paceSessions);
        return new CompareRiderSource(riderId, rider, row, snapshot);
    }

    private static JsonObject Result(
        string resultId,
        string riderId,
        string sessionId,
        string sessionType,
        int? position,
        string status,
        int? raceTimeMs
    )
    {
        var session = new JsonObject
        {
            ["id"] = sessionId,
            ["round_id"] = sessionId,
            ["name"] = sessionType,
            ["session_type"] = sessionType,
            ["session_date"] = $"2025-06-{int.Parse(sessionId) / 100:00}",
            ["categories"] = new JsonObject { ["id"] = "1", ["code"] = "EM" },
            ["rounds"] = new JsonObject
            {
                ["id"] = sessionId,
                ["season_year"] = 2025,
                ["round_number"] = int.Parse(sessionId) / 100,
                ["start_date"] = "2025-06-01",
                ["track_id"] = "7",
                ["tracks"] = new JsonObject
                {
                    ["id"] = "7",
                    ["name"] = "Test Track",
                    ["venue"] = "Test Venue",
                    ["country_code"] = "FRA",
                },
            },
        };
        return new JsonObject
        {
            ["id"] = resultId,
            ["rider_id"] = riderId,
            ["session_id"] = sessionId,
            ["position"] = position,
            ["status"] = status,
            ["is_classified"] = status == "Finished",
            ["race_time_ms"] = raceTimeMs,
            ["teams"] = new JsonObject { ["id"] = riderId, ["name"] = $"Team {riderId}" },
            ["sessions"] = session,
        };
    }

    private static JsonObject Split(string resultId, int index, int splitRank, int sectorRank) =>
        new()
        {
            ["result_id"] = resultId,
            ["split_index"] = index,
            ["race_time"] = 50_000,
            ["split_rank"] = splitRank,
            ["sector_rank"] = sectorRank,
        };

    private static PacePrecomputedSessionData PaceSession(
        string riderId,
        string sessionId,
        double finishTime,
        params double[] sectorTimes
    )
    {
        var summary = new PaceFieldSummary(2, 50, Math.Log(50), 1, 1, 50, 51);
        return new PacePrecomputedSessionData(
            sessionId,
            "track:7|category:1",
            "1",
            sessionId == "200" ? PaceSessionType.Final : PaceSessionType.Qualifying,
            new PaceRiderRun(
                riderId,
                sectorTimes.Cast<double?>().ToArray(),
                FinishTimeSeconds: finishTime
            ),
            2,
            sectorTimes
                .Select((time, index) => new PacePrecomputedSector(index, time, index + 1, summary))
                .ToArray(),
            null,
            null,
            summary,
            sectorTimes.Select(_ => (PaceFieldSummary?)summary).ToArray()
        );
    }
}
