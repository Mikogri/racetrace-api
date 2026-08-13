using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Tests;

public sealed class SeasonSummaryAndPodiumPredictorTests
{
    [Fact]
    public void PredictionScoreUsesFortyFortyTwentyWeights()
    {
        var score = PodiumPredictorService.WeightedPredictionScore(
            currentRating: 90m,
            trackPace: 80m,
            previousRating: 70m);

        Assert.Equal(82m, score);
    }

    [Fact]
    public void PredictionScoreRenormalizesWhenAComponentIsMissing()
    {
        var score = PodiumPredictorService.WeightedPredictionScore(
            currentRating: null,
            trackPace: 80m,
            previousRating: 70m);

        Assert.Equal(76.666666666666666666666666667m, score);
    }

    [Fact]
    public void TrackPaceUsesAtMostCurrentAndTwoPreviousSeasons()
    {
        Assert.Equal(
            [2024, 2025, 2026],
            PodiumPredictorService.RecentTrackSeasons(2026));
    }

    [Fact]
    public void LowerConfidenceReducesPredictionScore()
    {
        var highConfidence = PodiumPredictorService.ConfidenceAdjustedScore(
            95m,
            90m);
        var lowConfidence = PodiumPredictorService.ConfidenceAdjustedScore(
            95m,
            40m);

        Assert.Equal(93.8m, highConfidence);
        Assert.Equal(87.8m, lowConfidence);
        Assert.True(highConfidence > lowConfidence);
    }

    [Fact]
    public void PodiumProbabilitiesFavorHigherScoresAndSumToThreePlaces()
    {
        var probabilities = PodiumPredictorService.CalculatePodiumProbabilities(
            [96d, 93d, 90d, 87d, 84d]);

        Assert.Equal(5, probabilities.Count);
        Assert.True(probabilities[0] > probabilities[1]);
        Assert.True(probabilities[1] > probabilities[2]);
        Assert.InRange(probabilities.Sum(), 2.999999d, 3.000001d);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EveryRiderIsOnThePodiumWhenFieldHasAtMostThreeRiders(int count)
    {
        var probabilities = PodiumPredictorService.CalculatePodiumProbabilities(
            Enumerable.Repeat(90d, count).ToArray());

        Assert.All(probabilities, probability => Assert.Equal(1d, probability));
    }

    [Fact]
    public void SummaryFiltersDefaultToSeasonFromCurrentDateAndMenElite()
    {
        var service = ParserOnlySeasonSummaryService(
            new DateTimeOffset(2028, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var season = service.ReadSeason(Query());
        var category = service.ReadCategory(Query());

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
            null!,
            null!,
            new FixedTimeProvider(now));

    private static QueryCollection Query(
        Dictionary<string, StringValues>? values = null) =>
        new(values ?? []);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
