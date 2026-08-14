using UCI_Stats_API.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using System.Text.Json.Nodes;

namespace UCI_Stats_API.Tests;

public sealed class RankingServiceTests
{
    [Fact]
    public void CompactRankingDataHydratesCalculatorInput()
    {
        var response = new JsonObject
        {
            ["v"] = 1,
            ["s"] = new JsonArray
            {
                new JsonArray(
                    10, 20, 30, "Final", "FINAL", "2025-06-01", 40, 2025, "2025-05-30", 2,
                    "EM", 7, 2, "WORLD_CUP", "World Cup", 5, "Dry", "Black Snake", "Val di Sole",
                    "ITA", 2100, 12.5)
            },
            ["d"] = new JsonArray
            {
                new JsonArray(
                    50, 100050, "Max", "Rider", "FRA", "https://image.test/rider.png", 60, "Factory Team",
                    "FRA20000102", "2000-01-02", false, "FW5", "male")
            },
            ["r"] = new JsonArray
            {
                new JsonArray(70, 50, 10, 200000, 65.5, 1, true, "Finished", 250, 60, "Factory Team", "FW5")
            },
            ["f"] = new JsonArray
            {
                new JsonArray(70, 50, 10, 200000, 65.5, 1, true, "Finished"),
                new JsonArray(71, 51, 10, 202000, 64.5, 2, true, "Finished")
            },
            ["x"] = new JsonArray
            {
                new JsonArray(70, 1, 45000, 54.3)
            }
        };

        var bundle = DomainQueryService.ParseRankingDataBundle(response);

        var session = Assert.Single(bundle.Sessions);
        Assert.Equal("20", session.String("round_id"));
        Assert.Equal("EM", session.Object("categories")?.String("code"));
        Assert.Equal("Black Snake", session.Object("rounds")?.Object("tracks")?.String("name"));
        Assert.Equal(2, session.Object("rounds")?.Int("competition_class_priority"));

        var result = Assert.Single(bundle.Results);
        Assert.Equal(2, bundle.FieldResults.Count);
        Assert.Equal("51", bundle.FieldResults[1].String("rider_id"));
        Assert.Equal("Max", result.Object("riders")?.String("first_name"));
        Assert.Equal("Factory Team", result.Object("teams")?.String("name"));
        Assert.Equal("FW5", result.Object("teams")?.String("code"));
        Assert.Equal("2000-01-02", result.Object("riders")?.String("birth_date"));
        Assert.Equal("male", result.Object("riders")?.String("gender"));
        Assert.Equal("FINAL", result.Object("sessions")?.String("session_type"));
        Assert.Equal(2, result.Object("sessions")?.Object("rounds")?.Int("competition_class_priority"));
        Assert.Equal(250m, bundle.PointsByResultId["70"]);

        var split = Assert.Single(bundle.Splits);
        Assert.Equal(45000, split.Int("race_time"));
        Assert.Equal(54.3m, split.Decimal("speed"));
    }

    [Fact]
    public void CompactRankingDataHydratesResultTeamFromSharedDictionary()
    {
        var response = new JsonObject
        {
            ["s"] = new JsonArray
            {
                new JsonArray(10, 20, 30, "Final", "FINAL", "2025-06-01", 40, 2025, "2025-05-30", 2, "EM")
            },
            ["d"] = new JsonArray
            {
                new JsonArray(50, 100050, "Max", "Rider", "FRA", null, 60, "Current Team", null, null, false, "CUR")
            },
            ["t"] = new JsonArray
            {
                new JsonArray(61, "Result Team", "RES")
            },
            ["r"] = new JsonArray
            {
                new JsonArray(70, 50, 10, 200000, 65.5, 1, true, "Finished", 250, 61)
            },
            ["f"] = new JsonArray(),
            ["x"] = new JsonArray()
        };

        var bundle = DomainQueryService.ParseRankingDataBundle(response);

        var team = Assert.Single(bundle.Results).Object("teams");
        Assert.NotNull(team);
        Assert.Equal("61", team.String("id"));
        Assert.Equal("Result Team", team.String("name"));
        Assert.Equal("RES", team.String("code"));
    }

    [Fact]
    public void CompactRankingDataUsesEmbeddedHistoricalResultTeam()
    {
        var response = new JsonObject
        {
            ["s"] = new JsonArray
            {
                new JsonArray(10, 20, 30, "Final", "FINAL", "2025-06-01", 40, 2025, "2025-05-30", 2, "EM")
            },
            ["d"] = new JsonArray
            {
                new JsonArray(50, 100050, "Max", "Rider", "FRA", null, 60, "Current Team", null, null, false, "CUR")
            },
            ["t"] = new JsonArray(),
            ["r"] = new JsonArray
            {
                new JsonArray(70, 50, 10, 200000, 65.5, 1, true, "Finished", 250, 61, "Historical Team", "HIS")
            },
            ["f"] = new JsonArray(),
            ["x"] = new JsonArray()
        };

        var team = Assert.Single(
                DomainQueryService.ParseRankingDataBundle(response).Results)
            .Object("teams");

        Assert.NotNull(team);
        Assert.Equal("61", team.String("id"));
        Assert.Equal("Historical Team", team.String("name"));
        Assert.Equal("HIS", team.String("code"));
    }

    [Fact]
    public void CompactRankingDataUsesCurrentRiderTeamWhenTeamDictionaryIsOmitted()
    {
        var response = new JsonObject
        {
            ["s"] = new JsonArray
            {
                new JsonArray(10, 20, 30, "Final", "FINAL", "2025-06-01", 40, 2025, "2025-05-30", 2, "EM")
            },
            ["d"] = new JsonArray
            {
                new JsonArray(50, 100050, "Max", "Rider", "FRA", null, 60, "Latest Team", null, null, false, "NEW")
            },
            ["t"] = new JsonArray(),
            ["r"] = new JsonArray
            {
                new JsonArray(70, 50, 10, 200000, 65.5, 1, true, "Finished", 250, 44)
            },
            ["f"] = new JsonArray(),
            ["x"] = new JsonArray()
        };

        var bundle = DomainQueryService.ParseRankingDataBundle(response);
        var team = Assert.Single(bundle.Results).Object("teams");

        Assert.NotNull(team);
        Assert.Equal("60", team.String("id"));
        Assert.Equal("Latest Team", team.String("name"));
        Assert.Equal("NEW", team.String("code"));
    }

    [Fact]
    public void RiderDetailRunDoesNotAppendFieldSectorsMissingForTargetRider()
    {
        var log40 = Math.Log(40);
        var response = new JsonObject
        {
            ["s"] = new JsonArray(new JsonArray(
                10, 20, 30, "Final", "FINAL", "2025-06-01", 40, 2025,
                "2025-05-30", 2, "EM", 1, 2, "WORLD_CUP", "World Cup",
                5, "Dry", "Track", "Venue", "FRA", 2000, 10)),
            ["d"] = new JsonArray(new JsonArray(
                50, 100050, "Test", "Rider", "FRA", null, null, null,
                null, null, false, null)),
            ["r"] = new JsonArray(new JsonArray(
                70, 50, 10, null, null, null, false, "DNF", 0, null)),
            ["f"] = new JsonArray(),
            ["x"] = new JsonArray(),
            ["t"] = new JsonArray(),
            ["a"] = new JsonArray(new JsonArray(
                70, 10, 20, null,
                null, null, null, null, null, null, null,
                null, null, null, null, null, null, null)),
            ["q"] = new JsonArray(
                SectorAggregate(70, 0, 40, 1, log40),
                SectorAggregate(70, 1, null, null, log40),
                SectorAggregate(70, 2, null, null, log40))
        };

        var bundle = DomainQueryService.ParseRiderDetailDataBundle(response);
        var session = Assert.Single(bundle.PaceSessions);

        Assert.Equal(3, session.Sectors.Count);
        Assert.Equal([40d], session.Run.SectorTimes);
    }

    [Fact]
    public void CompactSharedStatisticsHydratePrecomputedRankingSession()
    {
        var log40 = Math.Log(40);
        var response = new JsonObject
        {
            ["s"] = new JsonArray(new JsonArray(
                10, 20, 30, "Final", "FINAL", "2025-06-01", 40, 2025,
                "2025-05-30", 2, "EM")),
            ["d"] = new JsonArray(new JsonArray(
                50, 100050, "Test", "Rider", "FRA", null, null, null,
                null, null, false, null)),
            ["r"] = new JsonArray(new JsonArray(
                70, 50, 10, 40_000, 60, 1, true, "Finished", 0, null)),
            ["f"] = new JsonArray(),
            ["x"] = new JsonArray(),
            ["t"] = new JsonArray(),
            ["a"] = new JsonArray(new JsonArray(70, 10, 20, 1)),
            ["q"] = new JsonArray(new JsonArray(70, 0, 40, 1)),
            ["g"] = new JsonArray(new JsonArray(
                10, 0,
                20, 40, log40, 0.1, 1, 38, 39,
                20, 40, log40, 0.1, 1, 38, 39)),
            ["h"] = new JsonArray(new JsonArray(
                10,
                20, 58, Math.Log(58), 0.1, 1, 65, 64,
                20, 42, Math.Log(42), 0.1, 1, 40, 41))
        };

        var parsed = DomainQueryService.ParseRiderDetailDataBundle(response);
        var session = Assert.Single(parsed.PaceSessions);

        Assert.Equal(20, session.ClassifiedFieldSize);
        Assert.Equal(40, Assert.Single(session.Sectors).TimeSeconds);
        Assert.Equal(20, session.Sectors[0].Statistics.Count);
        Assert.Equal(58, session.SpeedStatistics?.Median);
        Assert.Equal(42, session.FinishStatistics?.Median);
    }

    [Fact]
    public void CompactSharedStatisticsKeepAllRidersInOneSession()
    {
        var response = new JsonObject
        {
            ["s"] = new JsonArray(new JsonArray(
                10, 20, 30, "Final", "FINAL", "2025-06-01", 40, 2025,
                "2025-05-30", 2, "EM")),
            ["d"] = new JsonArray(
                new JsonArray(50, 100050, "First", "Rider", "FRA", null, null, null, null, null, false, null),
                new JsonArray(51, 100051, "Second", "Rider", "GBR", null, null, null, null, null, false, null)),
            ["r"] = new JsonArray(
                new JsonArray(70, 50, 10, 40_000, 60, 1, true, "Finished", 0),
                new JsonArray(71, 51, 10, 41_000, 59, 2, true, "Finished", 0)),
            ["a"] = new JsonArray(
                new JsonArray(70, 10, 20, 1),
                new JsonArray(71, 10, 20, 2)),
            ["q"] = new JsonArray(
                new JsonArray(70, 0, 40, 1),
                new JsonArray(71, 0, 41, 2)),
            ["g"] = new JsonArray(new JsonArray(
                10, 0, 20, 40.5, Math.Log(40.5), 0.1, 1, 40, 41,
                20, 40.5, Math.Log(40.5), 0.1, 1, 40, 41)),
            ["h"] = new JsonArray()
        };

        var parsed = DomainQueryService.ParseRiderDetailDataBundle(response);

        Assert.Equal(2, parsed.PaceSessions.Count);
        Assert.Equal(
            ["50", "51"],
            parsed.PaceSessions.Select(session => session.Run.RiderId).Order().ToArray());
    }

    [Fact]
    public void RiderDetailRunPreservesMiddleSectorGap()
    {
        var log40 = Math.Log(40);
        var response = new JsonObject
        {
            ["s"] = new JsonArray(new JsonArray(
                10, 20, 30, "Final", "FINAL", "2025-06-01", 40, 2025,
                "2025-05-30", 2, "EM", 1, 2, "WORLD_CUP", "World Cup",
                5, "Dry", "Track", "Venue", "FRA", 2000, 10)),
            ["d"] = new JsonArray(new JsonArray(
                50, 100050, "Test", "Rider", "FRA", null, null, null,
                null, null, false, null)),
            ["r"] = new JsonArray(new JsonArray(
                70, 50, 10, 130000, null, 1, true, "Finished", 0, null)),
            ["f"] = new JsonArray(),
            ["x"] = new JsonArray(),
            ["t"] = new JsonArray(),
            ["a"] = new JsonArray(new JsonArray(
                70, 10, 20, null,
                null, null, null, null, null, null, null,
                null, null, null, null, null, null, null)),
            ["q"] = new JsonArray(
                SectorAggregate(70, 0, 40, 1, log40),
                SectorAggregate(70, 1, null, null, log40),
                SectorAggregate(70, 2, 50, 1, log40))
        };

        var bundle = DomainQueryService.ParseRiderDetailDataBundle(response);

        Assert.Equal([40d, null, 50d], Assert.Single(bundle.PaceSessions).Run.SectorTimes);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 5, 0)]
    [InlineData(1, 3, 33.33)]
    [InlineData(2, 3, 66.67)]
    [InlineData(3, 3, 100)]
    public void PercentageUsesTwoDecimalPlaces(
        int numerator,
        int denominator,
        decimal expected)
    {
        Assert.Equal(expected, RankingService.Percentage(numerator, denominator));
    }

    [Theory]
    [InlineData("Finished", true)]
    [InlineData("DNF", true)]
    [InlineData("DSQ", true)]
    [InlineData("DNS", false)]
    [InlineData("dns", false)]
    public void HasStartedExcludesOnlyDns(string status, bool expected)
    {
        var result = new JsonObject { ["status"] = status };

        Assert.Equal(expected, RankingService.HasStarted(result));
    }

    [Fact]
    public void ParticipationMetricsCountDnsFinalAsQualifiedButExcludeDnsQualifyingRound()
    {
        var results = new[]
        {
            Result("10", "2024-06-01", sessionId: "101", roundId: "1", sessionType: "FINAL", status: "Finished"),
            Result("10", "2024-06-01", sessionId: "102", roundId: "1", sessionType: "FINAL", status: "DNS"),
            Result("10", "2024-07-01", sessionId: "150", roundId: "2", sessionType: "Q1", status: "Finished"),
            Result("10", "2024-08-01", sessionId: "250", roundId: "3", sessionType: "Q1", status: "DNS")
        };

        var rates = RankingService.CalculateParticipationRates(results);

        Assert.Equal(2, rates.FinalsCount);
        Assert.Equal(50m, rates.QualifyingRate);
    }

    [Fact]
    public void EveryFinalStatusCountsAsQualified()
    {
        var results = new[]
        {
            Result("10", "2024-05-01", sessionId: "101", roundId: "1", sessionType: "FINAL", status: "Finished"),
            Result("10", "2024-06-01", sessionId: "102", roundId: "2", sessionType: "FINAL", status: "DNF"),
            Result("10", "2024-07-01", sessionId: "103", roundId: "3", sessionType: "FINAL", status: "DNS"),
            Result("10", "2024-08-01", sessionId: "104", roundId: "4", sessionType: "FINAL", status: "DSQ")
        };

        var rates = RankingService.CalculateParticipationRates(results);

        Assert.Equal(4, rates.FinalsCount);
        Assert.Equal(100m, rates.QualifyingRate);
    }

    [Fact]
    public void TeamFallbackUsesLatestHistoricalTeamNotLaterThanSelectedResult()
    {
        var selected = new[]
        {
            Result("10", "2024-09-01")
        };
        var historical = new[]
        {
            Result("10", "2024-06-01", "1", "OLD", "Old Team"),
            Result("10", "2024-08-01", "2", "CUR", "Current Team"),
            Result("10", "2025-01-01", "3", "NEW", "Future Team")
        };

        var histories = RankingService.BuildFallbackTeamHistories(selected, historical);
        var team = histories["10"].OfType<JsonObject>().First();

        Assert.Equal("CUR", team.String("code"));
        Assert.Equal("Current Team", team.String("name"));
    }

    [Fact]
    public void ConfidenceQueryIsUsedAsMinimumConfidence()
    {
        var service = ParserOnlyService();

        var filters = service.ReadMetricFilters(Query(new()
        {
            ["confidence"] = "81"
        }));

        Assert.Equal(81m, filters.MinConfidence);
        Assert.Null(filters.MaxConfidence);
    }

    [Fact]
    public void ConfidenceRangeSupportsCamelCaseParameters()
    {
        var service = ParserOnlyService();

        var filters = service.ReadMetricFilters(Query(new()
        {
            ["minConfidence"] = "81",
            ["maxConfidence"] = "92"
        }));

        Assert.Equal(81m, filters.MinConfidence);
        Assert.Equal(92m, filters.MaxConfidence);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("101")]
    public void ConfidenceOutsidePercentageRangeIsRejected(string confidence)
    {
        var service = ParserOnlyService();

        Assert.ThrowsAny<Exception>(() => service.ReadMetricFilters(Query(new()
        {
            ["confidence"] = confidence
        })));
    }

    private static RankingService ParserOnlyService() =>
        new(
            new QueryValidationService(),
            null!,
            null!,
            new RankingSingleFlight(),
            new MemoryCache(new MemoryCacheOptions()));

    private static JsonArray SectorAggregate(
        long resultId,
        int sectorIndex,
        double? time,
        int? rank,
        double logMedian) =>
        new(
            resultId, sectorIndex, time, rank,
            20, 40, logMedian, 0.01, 1, 39, 40,
            20, 40, logMedian, 0.01, 1, 39, 40);

    private static JsonObject Result(
        string riderId,
        string date,
        string? teamId = null,
        string? teamCode = null,
        string? teamName = null,
        string? sessionId = null,
        string? roundId = null,
        string? sessionType = null,
        string? status = null)
    {
        var result = new JsonObject
        {
            ["rider_id"] = riderId,
            ["sessions"] = new JsonObject
            {
                ["id"] = sessionId,
                ["round_id"] = roundId,
                ["session_type"] = sessionType,
                ["session_date"] = date
            },
            ["session_id"] = sessionId,
            ["status"] = status
        };

        if (teamId is not null)
        {
            result["teams"] = new JsonObject
            {
                ["id"] = teamId,
                ["code"] = teamCode,
                ["name"] = teamName
            };
        }

        return result;
    }

    private static JsonObject Session(string id, string sessionType) =>
        new()
        {
            ["id"] = id,
            ["session_type"] = sessionType
        };

    private static QueryCollection Query(Dictionary<string, StringValues> values) =>
        new(values);
}
