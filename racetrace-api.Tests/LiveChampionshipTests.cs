using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class LiveChampionshipTests
{
    private static readonly ChampionshipStage Final = new("FINAL", [new(1, 1, 200), new(2, 2, 160), new(3, 3, 140)]);
    private static ChampionshipRider Rider(string id, int points) => new(id, id, id, points);
    private static ChampionshipRun Run(string id, int? position, string state = "finished") => new(id, id, position, state);
    private static ChampionshipSession Session(string type, params ChampionshipRun[] rows) => new(type, true, rows);

    [Fact]
    public void AddsQualifyingAndFinalPointsAndReturnsOnlyTopTwo()
    {
        var result = LiveChampionshipCalculator.Calculate(
            [Rider("A", 1000), Rider("B", 1100), Rider("C", 50)],
            [new("QUALI", [new(1, 1, 50), new(2, 2, 40)]), Final],
            [Session("QUALI", Run("A", 1), Run("B", 2)), Session("FINAL", Run("A", 1), Run("B", 2), Run("C", 3))]);
        Assert.Equal(["B", "A"], result.Items.Select(x => x.UciId));
        Assert.Equal(1300, result.Items[0].Points);
        Assert.Equal(50, result.Lead);
        Assert.False(result.CanChange);
    }

    [Fact]
    public void OnTrackSplitPositionNeverAwardsFinishPoints()
    {
        var result = LiveChampionshipCalculator.Calculate([Rider("A", 500), Rider("B", 600)], [Final],
            [new("FINAL", false, [Run("A", 1), Run("B", 1, "pending")])]);
        Assert.Equal(700, result.Items[0].Points);
        Assert.Equal(600, result.Items[1].Points);
        Assert.True(result.CanChange);
    }

    [Fact]
    public void ConsidersThirdPlaceChallengerAndPossibleDemotionOfFinishedLeader()
    {
        var result = LiveChampionshipCalculator.Calculate([Rider("A", 500), Rider("B", 530), Rider("C", 510)], [Final],
            [new("FINAL", false, [Run("A", 1), Run("B", 2), Run("C", null, "pending")])]);
        Assert.Equal(["A", "B"], result.Items.Select(x => x.UciId));
        Assert.True(result.CanChange); // C can score 710 while A drops to 660.
    }

    [Fact]
    public void ClinchedTitleAllowsRemainingRidersButNotTies()
    {
        var sessions = new[] { new ChampionshipSession("FINAL", false, [Run("A", 1), Run("B", null, "pending")]) };
        Assert.False(LiveChampionshipCalculator.Calculate([Rider("A", 1000), Rider("B", 500)], [Final], sessions).CanChange);
        Assert.True(LiveChampionshipCalculator.Calculate([Rider("A", 540), Rider("B", 500)], [Final], sessions).CanChange);
        var tied = LiveChampionshipCalculator.Calculate([Rider("A", 500), Rider("B", 540)], [Final],
            [Session("FINAL", Run("A", 1), Run("B", 2))]);
        Assert.Null(tied.CanChange);
        Assert.Equal("tied", tied.Status);
    }

    [Fact]
    public void FinishedChallengerBehindLeaderMustAlsoLosePointsWhenBothAreOvertaken()
    {
        var result = LiveChampionshipCalculator.Calculate([Rider("A", 500), Rider("B", 510), Rider("C", 0)], [Final],
            [new("FINAL", false, [Run("A", 1), Run("B", 2), Run("C", null, "pending")])]);
        Assert.False(result.CanChange); // If C wins: A=660, B=650. B cannot keep second-place points.
    }

    [Fact]
    public void NonFinishersHaveNoPointsAndMissingStagesOrIdentityCannotSecureTitle()
    {
        var result = LiveChampionshipCalculator.Calculate([Rider("A", 500), Rider("B", 540)], [Final],
            [Session("FINAL", Run("A", 1), Run("B", 2, "dnf"))]);
        Assert.Equal(540, result.Items[1].Points);
        Assert.False(result.CanChange);
        Assert.Null(LiveChampionshipCalculator.Calculate([Rider("A", 500)], [Final],
            [Session("FINAL", new ChampionshipRun(null, "Unknown", 1, "finished"))]).CanChange);
        Assert.Null(LiveChampionshipCalculator.Calculate([Rider("A", 500)], [Final, new("QUALI", [new(1, 1, 50)])],
            [Session("FINAL", Run("A", 1))]).CanChange);
    }
}
