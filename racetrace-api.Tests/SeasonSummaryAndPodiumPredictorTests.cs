using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class SeasonSummaryAndPodiumPredictorTests
{
    [Fact]
    public void LowConsistencyIncreasesUncertaintyWithoutChangingRawSkill()
    {
        var options = new PodiumPredictionOptions();
        var calibration = new PodiumPredictionCalibration(options);
        var stable = Feature("stable", consistency: 95, confidence: 100);
        var variable = Feature("variable", consistency: 40, confidence: 100);

        var result = calibration.Calculate([stable, variable], false);

        Assert.Equal(result[0].ExpectedPerformance, result[1].ExpectedPerformance, precision: 10);
        Assert.True(result[0].PerformanceUncertainty < result[1].PerformanceUncertainty);
    }

    [Fact]
    public void LowConfidenceShrinksSkillTowardFieldAndIncreasesUncertainty()
    {
        var calibration = new PodiumPredictionCalibration(new PodiumPredictionOptions());
        var reliable = Feature("reliable", rating: 95, pace: 95, confidence: 100);
        var uncertain = Feature("uncertain", rating: 95, pace: 95, confidence: 25);
        var field = Feature("field", rating: 70, pace: 70, confidence: 100);

        var result = calibration.Calculate([reliable, uncertain, field], false);

        Assert.True(result[1].ExpectedPerformance < result[0].ExpectedPerformance);
        Assert.True(result[1].PerformanceUncertainty > result[0].PerformanceUncertainty);
    }

    [Fact]
    public void WorldChampionshipIncreasesUncertainty()
    {
        var calibration = new PodiumPredictionCalibration(new PodiumPredictionOptions());
        var feature = Feature("rider");

        var worldCup = calibration.Calculate([feature], false)[0];
        var worldChampionship = calibration.Calculate([feature], true)[0];

        Assert.True(worldChampionship.PerformanceUncertainty > worldCup.PerformanceUncertainty);
    }

    [Fact]
    public void MonteCarloIsDeterministicAndProducesCoherentProbabilities()
    {
        var options = new PodiumPredictionOptions
        {
            MonteCarloIterations = 2_000,
            RandomSeed = 42
        };
        var distributions = new PodiumPredictionCalibration(options).Calculate(
            Enumerable.Range(0, 8)
                .Select(index => Feature(index.ToString(), 95 - index * 3, 95 - index * 3))
                .ToArray(),
            false);
        var simulator = new MonteCarloRaceSimulator(options);

        var first = simulator.Simulate(distributions);
        var second = simulator.Simulate(distributions);

        Assert.Equal(first, second);
        Assert.InRange(first.Sum(value => value.WinProbability), 0.999999, 1.000001);
        Assert.InRange(first.Sum(value => value.PodiumProbability), 2.999999, 3.000001);
        Assert.InRange(first.Sum(value => value.Top5Probability), 4.999999, 5.000001);
    }

    [Fact]
    public void TrackPaceUsesAtMostCurrentAndTwoPreviousSeasons()
    {
        Assert.Equal(
            [2024, 2025, 2026],
            PodiumPredictorService.RecentTrackSeasons(2026));
    }

    [Theory]
    [InlineData("JM")]
    [InlineData("JW")]
    [InlineData("jm")]
    public void JuniorCategoriesDoNotHavePodiumPrediction(string categoryCode)
    {
        Assert.True(PodiumPredictorService.IsJuniorCategory(categoryCode));
    }

    [Theory]
    [InlineData("EM")]
    [InlineData("EW")]
    public void EliteCategoriesHavePodiumPrediction(string categoryCode)
    {
        Assert.False(PodiumPredictorService.IsJuniorCategory(categoryCode));
    }

    [Theory]
    [InlineData("2007-12-31", 2025, true)]
    [InlineData("2006-12-31", 2025, false)]
    public void PreviousSeasonJuniorIsResolvedFromAge(
        string birthDate,
        int season,
        bool expectedJunior)
    {
        Assert.Equal(
            expectedJunior,
            RiderCategoryResolver.WasJuniorInSeason(
                DateOnly.Parse(birthDate),
                season));
    }

    [Theory]
    [InlineData("male", "2008-08-21", "junior_men")]
    [InlineData("female", "2008-08-21", "junior_women")]
    [InlineData("male", "2000-01-01", "elite_men")]
    [InlineData("female", "2000-01-01", "elite_women")]
    public void CurrentCategoryIsResolvedFromAgeAndGender(
        string gender,
        string birthDate,
        string expectedCategory)
    {
        var rider = new JsonObject
        {
            ["gender"] = gender,
            ["birth_date"] = birthDate
        };

        Assert.Equal(
            expectedCategory,
            RiderCategoryResolver.CurrentCategory(
                rider,
                new DateOnly(2026, 8, 20)));
    }

    [Fact]
    public async Task SummaryFiltersDefaultToSeasonFromCurrentDateAndMenElite()
    {
        var service = ParserOnlySeasonSummaryService(
            new DateTimeOffset(2028, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var season = service.ReadSeason(Query());
        var category = await service.ReadCategoryAsync(Query(), default);

        Assert.Equal(2028, season);
        Assert.Equal("elite_men", category.PublicCategory);
        Assert.Equal("EM", category.DatabaseCategory);
    }

    [Fact]
    public void SummaryRoundContainsTrackAndCompetitionClassDetails()
    {
        var round = new JsonObject
        {
            ["id"] = "12",
            ["season_year"] = 2026,
            ["round_number"] = 3,
            ["start_date"] = "2026-08-01",
            ["end_date"] = "2026-08-02",
            ["track_id"] = "7",
            ["tracks"] = new JsonObject
            {
                ["id"] = "7",
                ["name"] = "Black Snake",
                ["venue"] = "Val di Sole",
                ["country_code"] = "ITA",
                ["length_m"] = 2100,
                ["avg_slope"] = 24.5m
            },
            ["competition_class_id"] = "2",
            ["competition_class_priority"] = 2,
            ["competition_class"] = new JsonObject
            {
                ["id"] = "2",
                ["code"] = "world_cup",
                ["name"] = "World Cup",
                ["priority"] = 2
            }
        };

        var dto = SeasonSummaryService.ToRoundDto(
            round,
            new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(12, dto.RoundId);
        Assert.Equal("Black Snake", dto.TrackName);
        Assert.Equal("Val di Sole", dto.VenueName);
        Assert.Equal(2100, dto.TrackLengthMeters);
        Assert.Equal(24.5m, dto.AverageSlope);
        Assert.Equal("world_cup", dto.CompetitionClassCode);
    }

    private static SeasonSummaryService ParserOnlySeasonSummaryService(
        DateTimeOffset now) =>
        new(
            null!,
            new QueryValidationService(),
            new DomainQueryService(null!),
            null!,
            new FixedTimeProvider(now));

    private static QueryCollection Query(
        Dictionary<string, StringValues>? values = null) =>
        new(values ?? []);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static PodiumPredictionFeatures Feature(
        string riderId,
        double rating = 85,
        double pace = 85,
        double consistency = 80,
        double confidence = 80) =>
        new(
            riderId,
            (decimal)rating,
            null,
            null,
            rating,
            pace,
            confidence,
            consistency,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0.08,
            12);
}
