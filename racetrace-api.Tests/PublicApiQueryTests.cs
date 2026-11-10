using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class PublicApiQueryTests
{
    [Fact]
    public void SessionResultsPutUnclassifiedStatusesAfterPositionsInRequiredOrder()
    {
        JsonObject Row(string id, int? position, string status) =>
            new()
            {
                ["id"] = id,
                ["position"] = position,
                ["status"] = status,
            };

        var rows = new[]
        {
            Row("5", null, "DSQ"),
            Row("2", 2, "Finished"),
            Row("4", null, "DNS"),
            Row("1", 1, "Finished"),
            Row("3", null, "DNF"),
        };

        var sorted = ResultsService.SortByResultPosition(rows, descending: true);

        Assert.Equal(["1", "2", "3", "4", "5"], sorted.Select(row => row.String("id")));
    }

    [Fact]
    public void DescendingPositionStillKeepsUnclassifiedStatusesAtTheEnd()
    {
        var rows = new[]
        {
            new JsonObject
            {
                ["id"] = "1",
                ["position"] = 1,
                ["status"] = "Finished",
            },
            new JsonObject
            {
                ["id"] = "3",
                ["position"] = null,
                ["status"] = "DNF",
            },
            new JsonObject
            {
                ["id"] = "2",
                ["position"] = 2,
                ["status"] = "Finished",
            },
        };

        var sorted = ResultsService.SortByResultPosition(rows, descending: true);

        Assert.Equal(["1", "2", "3"], sorted.Select(row => row.String("id")));
    }

    [Fact]
    public void DescendingTimingSortUsesBestSectorPositionFirst()
    {
        JsonObject Row(string id, int? sectorPosition) =>
            new()
            {
                ["id"] = id,
                ["status"] = "Finished",
                ["sectors"] = new JsonArray(
                    new JsonObject { ["split_index"] = 2, ["sector_position"] = sectorPosition }
                ),
            };

        var sorted = ResultsService.SortByTimingPosition(
            [Row("slow", 8), Row("missing", null), Row("fast", 1)],
            "sectors",
            2,
            descending: true
        );

        Assert.Equal(["fast", "slow", "missing"], sorted.Select(row => row.String("id")));
    }

    [Fact]
    public void DnfWithTimingIsTreatedAsSlowestInSectorAndSplitSorting()
    {
        JsonObject Row(string id, string status, int timingPosition) =>
            new()
            {
                ["id"] = id,
                ["status"] = status,
                ["position"] = status == "Finished" ? 2 : null,
                ["sectors"] = new JsonArray(
                    new JsonObject { ["split_index"] = 1, ["sector_position"] = timingPosition }
                ),
                ["splits"] = new JsonArray(
                    new JsonObject { ["split_index"] = 1, ["split_position"] = timingPosition }
                ),
            };

        var dnf = Row("dnf", "DNF", 1);
        var finished = Row("finished", "Finished", 2);

        Assert.Equal(
            ["finished", "dnf"],
            ResultsService
                .SortByTimingPosition([finished, dnf], "sectors", 1, true)
                .Select(row => row.String("id"))
        );
        Assert.Equal(
            ["finished", "dnf"],
            ResultsService
                .SortByTimingPosition([finished, dnf], "splits", 1, true)
                .Select(row => row.String("id"))
        );
        Assert.Equal(
            ["dnf", "finished"],
            ResultsService
                .SortByTimingPosition([finished, dnf], "sectors", 1, false)
                .Select(row => row.String("id"))
        );
        Assert.Equal(
            ["dnf", "finished"],
            ResultsService
                .SortByTimingPosition([finished, dnf], "splits", 1, false)
                .Select(row => row.String("id"))
        );
    }

    [Fact]
    public void DescendingRaceTimeSortUsesLowestTimeFirst()
    {
        var rows = new[]
        {
            new JsonObject
            {
                ["id"] = "slow",
                ["race_time_ms"] = 70_000,
                ["status"] = "Finished",
            },
            new JsonObject
            {
                ["id"] = "dnf",
                ["race_time_ms"] = null,
                ["status"] = "DNF",
            },
            new JsonObject
            {
                ["id"] = "fast",
                ["race_time_ms"] = 60_000,
                ["status"] = "Finished",
            },
        };

        var sorted = ResultsService.SortByRaceTime(rows, descending: true);

        Assert.Equal(["fast", "slow", "dnf"], sorted.Select(row => row.String("id")));
    }

    [Fact]
    public void AscendingRaceTimeSortPutsUnclassifiedStatusesBeforeSlowestFinishedTime()
    {
        var rows = new[]
        {
            new JsonObject
            {
                ["id"] = "fast",
                ["race_time_ms"] = 60_000,
                ["status"] = "Finished",
            },
            new JsonObject
            {
                ["id"] = "dns",
                ["race_time_ms"] = null,
                ["status"] = "DNS",
            },
            new JsonObject
            {
                ["id"] = "slow",
                ["race_time_ms"] = 70_000,
                ["status"] = "Finished",
            },
            new JsonObject
            {
                ["id"] = "dnf",
                ["race_time_ms"] = 90_000,
                ["status"] = "DNF",
            },
            new JsonObject
            {
                ["id"] = "dsq",
                ["race_time_ms"] = 65_000,
                ["status"] = "DSQ",
            },
        };

        var sorted = ResultsService.SortByRaceTime(rows, descending: false);

        Assert.Equal(["dnf", "dns", "dsq", "slow", "fast"], sorted.Select(row => row.String("id")));
    }

    [Fact]
    public void ParsesCamelCaseMultiValueFilters()
    {
        var query = Query(
            new Dictionary<string, StringValues>
            {
                ["season"] = new StringValues(["2025", "2026"]),
                ["trackId"] = "1,4",
                ["conditionId"] = "2,3",
                ["competitionClassId"] = "1,2",
                ["category"] = "elite_men,junior_women",
                ["sessionType"] = "qualifying_1,final",
                ["dateFrom"] = "2025-01-01",
                ["dateTo"] = "2026-12-31",
            }
        );

        var context = DomainQueryService.MetricsContextFromQuery(
            query,
            new QueryValidationService()
        );

        Assert.Equal([2025, 2026], context.SeasonYears);
        Assert.Equal(["1", "4"], context.TrackIds);
        Assert.Equal([2, 3], context.ConditionIds);
        Assert.Equal([1, 2], context.CompetitionClassIds);
        Assert.Equal(["EM", "JW"], context.CategoryCodes);
        Assert.Equal(["Q1", "FINAL"], context.SessionTypes);
        Assert.Equal(new DateOnly(2025, 1, 1), context.FromDate);
        Assert.Equal(new DateOnly(2026, 12, 31), context.ToDate);
    }

    [Fact]
    public void ParsesCompareFilterAliases()
    {
        var context = DomainQueryService.MetricsContextFromQuery(
            Query(
                new Dictionary<string, StringValues>
                {
                    ["seasons"] = "2024,2025",
                    ["tracks"] = "7,9",
                    ["conditions"] = "2,3",
                    ["class"] = "world_cup,world_championship",
                }
            ),
            new QueryValidationService()
        );

        Assert.Equal([2024, 2025], context.SeasonYears);
        Assert.Equal(["7", "9"], context.TrackIds);
        Assert.Equal([2, 3], context.ConditionIds);
        Assert.Equal(["world_cup", "world_championship"], context.CompetitionClassCodes);
    }

    [Fact]
    public void ParsesCategoryListAliasesUsedByCompareAndRiderDetails()
    {
        var context = DomainQueryService.MetricsContextFromQuery(
            Query(new Dictionary<string, StringValues> { ["categories"] = "junior_men,elite_men" }),
            new QueryValidationService()
        );

        Assert.Equal(["JM", "EM"], context.CategoryCodes);
    }

    [Fact]
    public void PageSizeDefaultsToThirtyAndSupportsLegacyLimit()
    {
        var validator = new QueryValidationService();

        Assert.Equal(30, validator.Page(Query([])).Limit);
        Assert.Equal(
            75,
            validator
                .Page(Query(new Dictionary<string, StringValues> { ["pageSize"] = "75" }))
                .Limit
        );
        Assert.Equal(
            150,
            validator.Page(Query(new Dictionary<string, StringValues> { ["limit"] = "150" })).Limit
        );
        Assert.Throws<ApiException>(() =>
            validator.Page(Query(new Dictionary<string, StringValues> { ["pageSize"] = "151" }))
        );
    }

    [Fact]
    public void RejectsInvalidCategorySessionAndDateRange()
    {
        var validator = new QueryValidationService();

        Assert.Throws<ApiException>(() =>
            DomainQueryService.MetricsContextFromQuery(
                Query(new Dictionary<string, StringValues> { ["category"] = "masters" }),
                validator
            )
        );
        Assert.Throws<ApiException>(() =>
            DomainQueryService.MetricsContextFromQuery(
                Query(new Dictionary<string, StringValues> { ["sessionType"] = "sprint" }),
                validator
            )
        );
        Assert.Throws<ApiException>(() =>
            DomainQueryService.MetricsContextFromQuery(
                Query(
                    new Dictionary<string, StringValues>
                    {
                        ["dateFrom"] = "2026-12-31",
                        ["dateTo"] = "2026-01-01",
                    }
                ),
                validator
            )
        );
    }

    [Theory]
    [InlineData("EM", "elite_men")]
    [InlineData("EW", "elite_women")]
    [InlineData("JM", "junior_men")]
    [InlineData("JW", "junior_women")]
    public void MapsAllPublicCategories(string databaseCode, string publicCode)
    {
        Assert.Equal(publicCode, PublicApiCodes.ToPublicCategory(databaseCode));
        Assert.Equal(databaseCode, PublicApiCodes.ToDatabaseCategory(publicCode));
    }

    [Theory]
    [InlineData("ME", "EM", "elite_men")]
    [InlineData("WE", "EW", "elite_women")]
    [InlineData("MJ", "JM", "junior_men")]
    [InlineData("WJ", "JW", "junior_women")]
    public void LegacyCategoryCodesMapToCanonicalValues(
        string legacyCode,
        string databaseCode,
        string publicCode
    )
    {
        Assert.Equal(databaseCode, PublicApiCodes.ToDatabaseCategory(legacyCode));
        Assert.Equal(publicCode, PublicApiCodes.ToPublicCategory(legacyCode));
    }

    [Fact]
    public void PublicDtosUseCamelCaseApplicationPolicy()
    {
        var response = PublicPagedResponse<TrackFilterOptionDto>.Create(
            [new TrackFilterOptionDto(4, "Black Snake", 4, "Val di Sole", "ITA", 2100, 23.5m)],
            1,
            30,
            40
        );
        var json = JsonSerializer.Serialize(
            response,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
        );

        Assert.Contains("\"pageSize\":30", json, StringComparison.Ordinal);
        Assert.Contains("\"totalItems\":40", json, StringComparison.Ordinal);
        Assert.Contains("\"trackName\":\"Black Snake\"", json, StringComparison.Ordinal);
        Assert.Contains("\"lengthM\":2100", json, StringComparison.Ordinal);
        Assert.Contains("\"avgSlope\":23.5", json, StringComparison.Ordinal);
        Assert.DoesNotContain("page_size", json, StringComparison.Ordinal);
    }

    [Fact]
    public void RankingPositionIsPreservedWhenDisplayOrderIsAscending()
    {
        var rows = new[] { RiderRow("1", "Fast", 95m), RiderRow("2", "Slow", 80m) };

        RankingService.AssignCanonicalPositions(rows, "rating");
        var ascending = RankingService.SortRows(rows, "rating", descending: false);

        Assert.Equal("2", ascending[0].String("rider_id"));
        Assert.Equal(2, ascending[0].Int("rank"));
        Assert.Equal(1, ascending[1].Int("rank"));
    }

    [Fact]
    public void MissingSplitDoesNotBecomeZeroSectorTime()
    {
        var sectors = RidersService.BuildSectorRows([
            new JsonObject
            {
                ["result_id"] = "1",
                ["split_index"] = 1,
                ["race_time"] = 45_000,
            },
            new JsonObject
            {
                ["result_id"] = "1",
                ["split_index"] = 2,
                ["race_time"] = null,
            },
            new JsonObject
            {
                ["result_id"] = "1",
                ["split_index"] = 3,
                ["race_time"] = 90_000,
            },
        ]);

        Assert.Equal(45_000, sectors[0].Int("sector_time_ms"));
        Assert.Null(sectors[1].Int("sector_time_ms"));
        Assert.Null(sectors[2].Int("sector_time_ms"));
    }

    [Fact]
    public void RiderSectorStatisticsIncludeTheFinishSector()
    {
        var sectors = RidersService.BuildSectorRows(
            [
                new JsonObject
                {
                    ["result_id"] = "1",
                    ["split_index"] = 1,
                    ["race_time"] = 40_000,
                },
                new JsonObject
                {
                    ["result_id"] = "1",
                    ["split_index"] = 2,
                    ["race_time"] = 80_000,
                },
                new JsonObject
                {
                    ["result_id"] = "1",
                    ["split_index"] = 3,
                    ["race_time"] = 120_000,
                },
                new JsonObject
                {
                    ["result_id"] = "1",
                    ["split_index"] = 4,
                    ["race_time"] = 160_000,
                },
            ],
            [
                new JsonObject
                {
                    ["id"] = "1",
                    ["session_id"] = "10",
                    ["race_time_ms"] = 205_000,
                },
            ]
        );

        Assert.Equal(5, sectors.Count);
        Assert.Equal(5, sectors[^1].Int("split_index"));
        Assert.Equal(45_000, sectors[^1].Int("sector_time_ms"));
    }

    [Fact]
    public void CompletedResultExcludesDnfAndRequiresFinalTime()
    {
        Assert.True(
            ResultsService.IsCompletedResult(
                new JsonObject
                {
                    ["race_time_ms"] = 200_000,
                    ["position"] = 1,
                    ["is_classified"] = true,
                    ["status"] = "Finished",
                }
            )
        );
        Assert.False(
            ResultsService.IsCompletedResult(
                new JsonObject
                {
                    ["race_time_ms"] = 200_000,
                    ["position"] = 1,
                    ["is_classified"] = false,
                    ["status"] = "DNF",
                }
            )
        );
    }

    [Fact]
    public void RoundStatusUsesLiveSessionAndDates()
    {
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(
            "live",
            RoundsService.RoundStatus(
                new JsonObject
                {
                    ["sessions"] = new JsonArray(new JsonObject { ["status"] = "live" }),
                },
                now
            )
        );
        Assert.Equal(
            "upcoming",
            RoundsService.RoundStatus(
                new JsonObject { ["start_date"] = "2026-07-21T00:00:00Z" },
                now
            )
        );
        Assert.Equal(
            "completed",
            RoundsService.RoundStatus(new JsonObject { ["end_date"] = "2026-07-19T00:00:00Z" }, now)
        );
        Assert.Equal(
            "live",
            RoundsService.RoundStatus(
                new JsonObject
                {
                    ["start_date"] = "2026-07-19T00:00:00+02:00",
                    ["end_date"] = "2026-07-20T00:00:00+02:00",
                },
                now
            )
        );
    }

    [Fact]
    public void EmbeddedTrackRoundsAreSortedByDateWithoutRoundNumberInfluence()
    {
        var track = new JsonObject
        {
            ["rounds"] = new JsonArray(
                Round("9", 1, "2025-06-01"),
                Round("3", 99, "2026-07-01"),
                Round("4", 1, "2026-07-01"),
                Round("8", 8, "2024-05-01")
            ),
        };

        TracksService.SortEmbeddedRoundsByDate(track);

        Assert.Equal(
            ["4", "3", "9", "8"],
            track
                .Array("rounds")!
                .OfType<JsonObject>()
                .Select(round => round.String("id")!)
                .ToArray()
        );
    }

    [Theory]
    [InlineData("live")]
    [InlineData("confirmed")]
    [InlineData("cancelled")]
    public void ExplicitRoundStatusOverridesDates(string status)
    {
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(
            status,
            RoundsService.RoundStatus(
                new JsonObject
                {
                    ["status"] = status,
                    ["start_date"] = "2026-07-21T00:00:00Z",
                    ["end_date"] = "2026-07-22T00:00:00Z",
                },
                now
            )
        );
    }

    [Theory]
    [InlineData("active")]
    [InlineData("running")]
    public void ActiveRoundStatusAliasesArePublishedAsLive(string status)
    {
        Assert.Equal("live", RoundsService.RoundStatus(new JsonObject { ["status"] = status }));
    }

    [Fact]
    public void PublicSessionResultMapsSectorTimesToSecondsAndPreservesMissingValues()
    {
        var result = ResultWithTiming();
        result["sectors"] = new JsonArray(
            new JsonObject
            {
                ["split_index"] = 1,
                ["sector_time_ms"] = 35_123,
                ["sector_gap_to_leader_ms"] = 250,
                ["sector_position"] = 2,
                ["speed"] = 42.5m,
            },
            new JsonObject
            {
                ["split_index"] = 2,
                ["sector_time_ms"] = null,
                ["sector_gap_to_leader_ms"] = null,
                ["sector_position"] = null,
            }
        );

        var dto = ResultsService.ToPublicResult(result, "sectors", 4, trackLengthM: 2500);

        Assert.Equal(4, dto.Position);
        Assert.Equal(35.123m, dto.Times[0].Time);
        Assert.Equal(0.250m, dto.Times[0].GapToLeader);
        Assert.Equal(2, dto.Times[0].Position);
        Assert.Null(dto.Times[1].Time);
        Assert.Null(dto.Times[1].GapToLeader);
        Assert.Equal(12.5m, dto.AwardedPoints);
        Assert.Equal(43.810m, dto.AverageSpeed);
    }

    [Fact]
    public void PublicSessionResultMapsSplitTimesWithoutChangingThemIntoSectors()
    {
        var result = ResultWithTiming();
        result["splits"] = new JsonArray(
            new JsonObject
            {
                ["split_index"] = 2,
                ["race_time_ms"] = 65_000,
                ["split_gap_to_leader_ms"] = 1_500,
                ["split_position"] = 3,
            }
        );

        var dto = ResultsService.ToPublicResult(result, "splits", 1);

        var split = Assert.Single(dto.Times);
        Assert.Equal("Split 2", split.Name);
        Assert.Equal(65m, split.Time);
        Assert.Equal(1.5m, split.GapToLeader);
        Assert.Equal(3, split.Position);
    }

    [Fact]
    public void AverageSpeedUsesTrackDistanceAndFinishTimeInsteadOfSplitSpeeds()
    {
        var result = ResultWithTiming();
        result["race_time_ms"] = 200_000;
        result["sectors"] = new JsonArray(
            new JsonObject { ["split_index"] = 1, ["speed"] = 10m },
            new JsonObject { ["split_index"] = 2, ["speed"] = 90m }
        );

        var dto = ResultsService.ToPublicResult(result, "sectors", 1, trackLengthM: 2500);

        Assert.Equal(45m, dto.AverageSpeed);
    }

    [Theory]
    [InlineData(null, 200000)]
    [InlineData(0, 200000)]
    [InlineData(2500, null)]
    [InlineData(2500, 0)]
    public void AverageSpeedIsMissingWithoutValidDistanceAndTime(int? trackLengthM, int? raceTimeMs)
    {
        Assert.Null(ResultsService.CalculateAverageSpeedKmh(trackLengthM, raceTimeMs));
    }

    [Fact]
    public void HistoricalQualifyingIsNotMappedToQualifyingOne()
    {
        var qualifying = RiderResult("QUALI", 4);
        var qualifyingOne = RiderResult("Q1", 2);

        Assert.Equal(
            4m,
            RidersService.AveragePositionForType([qualifying, qualifyingOne], "qualifying")
        );
        Assert.Equal(
            2m,
            RidersService.AveragePositionForType([qualifying, qualifyingOne], "qualifying_1")
        );
    }

    [Fact]
    public void RiderRoundResultUsesStoredAwardedPointsAndCompetitionClass()
    {
        var result = RiderResult("FINAL", 1);
        result["awarded_points"] = 250m;
        result["sessions"]!["rounds"]!["competition_level_id"] = "2";
        result["sessions"]!["rounds"]!["competition_levels"] = new JsonObject
        {
            ["id"] = "2",
            ["code"] = "world_cup",
            ["name"] = "World Cup",
        };

        var dto = RidersService.ToRoundResult("9", [result], null);

        Assert.Equal(250m, dto.AwardedPoints);
        Assert.Equal(2, dto.CompetitionClassId);
        Assert.Equal("world_cup", dto.CompetitionClassCode);
        Assert.Equal(1, dto.FinalPosition);
    }

    private static QueryCollection Query(Dictionary<string, StringValues> values) => new(values);

    private static JsonObject RiderRow(string id, string name, decimal rating) =>
        new()
        {
            ["rider_id"] = id,
            ["rider_name"] = name,
            ["rating"] = rating,
        };

    private static JsonObject Round(string id, int roundNumber, string startDate) =>
        new()
        {
            ["id"] = id,
            ["round_number"] = roundNumber,
            ["start_date"] = startDate,
        };

    private static JsonObject ResultWithTiming() =>
        new()
        {
            ["id"] = "441",
            ["rider_id"] = "41",
            ["team_id"] = "16",
            ["status"] = "Finished",
            ["race_time_ms"] = 205_432,
            ["gap_ms"] = 0,
            ["awarded_points"] = 12.5m,
            ["riders"] = new JsonObject
            {
                ["id"] = "41",
                ["uci_id"] = "10004535237",
                ["first_name"] = "Loic",
                ["last_name"] = "Bruni",
                ["nation"] = "FRA",
            },
            ["teams"] = new JsonObject { ["id"] = "16", ["name"] = "Specialized Gravity" },
        };

    private static JsonObject RiderResult(string sessionType, int position) =>
        new()
        {
            ["id"] = Guid.NewGuid().ToString("N"),
            ["position"] = position,
            ["sessions"] = new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString("N"),
                ["session_type"] = sessionType,
                ["rounds"] = new JsonObject
                {
                    ["id"] = "9",
                    ["season_year"] = 2024,
                    ["round_number"] = 3,
                    ["start_date"] = "2024-06-01",
                    ["tracks"] = new JsonObject
                    {
                        ["id"] = "5",
                        ["name"] = "Black Snake",
                        ["venue"] = "Val di Sole",
                        ["country_code"] = "ITA",
                    },
                },
            },
        };
}
