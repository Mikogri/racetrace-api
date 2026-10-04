using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class RiderSimilarityTests
{
    [Fact]
    public void ProfileSeparatesStrongStartFromWeakFinish()
    {
        var observations = new[]
        {
            Observation("s1", 0, 3, 95),
            Observation("s1", 1, 3, 70),
            Observation("s1", 2, 3, 45),
            Observation("s2", 0, 3, 90),
            Observation("s2", 1, 3, 72),
            Observation("s2", 2, 3, 40)
        };

        var profile = RiderProfileBuilder.Build("1", "1", "EM", observations, 6, 2)!;

        Assert.True(profile.Profile.Start > profile.Profile.Middle);
        Assert.True(profile.Profile.Middle > profile.Profile.Finish);
    }

    [Fact]
    public void IdenticalProfilesHaveSimilarityOne()
    {
        var profiles = new[]
        {
            Profile("1", 0.8, 0.7, 0.6),
            Profile("2", 0.8, 0.7, 0.6)
        };

        var result = RiderSimilarityCalculator.Analyze(profiles, 4, 1, true);

        Assert.Equal(1, result.Pairs.Single().Similarity);
    }

    [Fact]
    public void OppositeProfilesHaveLowSimilarity()
    {
        var profiles = new[]
        {
            Profile("1", 0.95, 0.8, 0.3),
            Profile("2", 0.3, 0.8, 0.95)
        };

        var result = RiderSimilarityCalculator.Analyze(profiles, 4, 1, true);

        Assert.InRange(result.Pairs.Single().Similarity!.Value, 0, 0.25);
    }

    [Fact]
    public void MissingFeatureIsNotReplacedWithZero()
    {
        var first = Profile("1", 0.9, 0.8, 0.7);
        var second = Profile("2", 0.9, 0.8, 0.7) with
        {
            Profile = Profile("2", 0.9, 0.8, 0.7).Profile with { Finish = null }
        };

        var result = RiderSimilarityCalculator.Analyze([first, second], 4, 1, true);
        var pair = result.Pairs.Single();

        Assert.Equal(6, pair.SharedFeatures);
        Assert.NotNull(pair.Similarity);
    }

    [Fact]
    public void TooFewSharedFeaturesReturnsNullSimilarity()
    {
        var first = Profile("1", 0.9, 0.8, 0.7);
        var second = Profile("2", 0.9, 0.8, 0.7) with
        {
            Profile = new RiderSectorProfileDto(0.9, null, null, null, null, null, null, null, null)
        };

        var pair = RiderSimilarityCalculator.Analyze([first, second], 4, 1, true).Pairs.Single();

        Assert.Null(pair.Similarity);
        Assert.Equal(1, pair.SharedFeatures);
    }

    [Fact]
    public void SimilarProfilesMergeBeforeDifferentProfile()
    {
        var profiles = new[]
        {
            Profile("1", 0.90, 0.70, 0.50),
            Profile("2", 0.88, 0.71, 0.52),
            Profile("3", 0.40, 0.65, 0.95)
        };

        var result = RiderSimilarityCalculator.Analyze(profiles, 4, 2, true);
        var firstMerge = result.Merges.First();

        Assert.Equal(new HashSet<string>(["1", "2"]), new HashSet<string>([firstMerge.LeftId, firstMerge.RightId]));
    }

    [Fact]
    public void CategoriesAreNotComparedWithoutExplicitCrossCategorySelection()
    {
        var profiles = new[]
        {
            Profile("1", 0.9, 0.8, 0.7) with { CategoryCode = "EM" },
            Profile("2", 0.9, 0.8, 0.7) with { CategoryCode = "EW" }
        };

        var pair = RiderSimilarityCalculator.Analyze(profiles, 4, null, false).Pairs.Single();

        Assert.Null(pair.Similarity);
        Assert.Equal(0, pair.SharedFeatures);
    }

    [Fact]
    public void MultipleSelectedCategoriesStillRemainSeparated()
    {
        var profiles = new[]
        {
            Profile("1", 0.9, 0.8, 0.7) with { CategoryCode = "EM" },
            Profile("2", 0.9, 0.8, 0.7) with { CategoryCode = "EW" }
        };

        var analysis = RiderSimilarityCalculator.Analyze(
            profiles,
            minimumSharedFeatures: 4,
            requestedClusterCount: 2,
            allowCrossCategory: false);
        var pair = analysis.Pairs.Single();

        Assert.Null(pair.Similarity);
        Assert.NotEqual(
            analysis.ClusterByProfileId["1"],
            analysis.ClusterByProfileId["2"]);
    }

    private static RiderSectorObservation Observation(
        string sessionId,
        int sectorIndex,
        int sectorCount,
        double score) =>
        new("1", sessionId, "EM", sectorIndex, sectorCount, score);

    private static RiderSimilarityProfile Profile(
        string id,
        double start,
        double middle,
        double finish) =>
        new(
            id,
            id,
            "EM",
            20,
            4,
            1,
            new RiderSectorProfileDto(
                start,
                middle,
                finish,
                null,
                null,
                0.8,
                0.9,
                0.6,
                0.1));
}
