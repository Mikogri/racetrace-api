using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class DownhillResultScoreCalculatorTests
{
    [Fact]
    public void PositionScoreMapsWinnerToHundredAndLastToZero()
    {
        var calculator = new DownhillResultScoreCalculator();

        Assert.Equal(100, calculator.CalculatePositionScore(rank: 1, fieldSize: 100), precision: 10);
        Assert.Equal(0, calculator.CalculatePositionScore(rank: 100, fieldSize: 100), precision: 10);
    }

    [Fact]
    public void PositionScoreStronglySeparatesTopFive()
    {
        var calculator = new DownhillResultScoreCalculator();

        var scores = Enumerable.Range(1, 5)
            .Select(rank => calculator.CalculatePositionScore(rank, fieldSize: 60))
            .ToArray();

        Assert.Equal(100, scores[0], precision: 10);
        Assert.Equal(96, scores[1], precision: 10);
        Assert.InRange(scores[2], 93, 94);
        Assert.True(scores[0] > scores[1]);
        Assert.True(scores[1] > scores[2]);
        Assert.True(scores[2] > scores[3]);
        Assert.True(scores[3] > scores[4]);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(100)]
    public void SecondPlaceAnchorIsStableAcrossFieldSizes(int fieldSize)
    {
        var calculator = new DownhillResultScoreCalculator();

        Assert.Equal(
            96,
            calculator.CalculatePositionScore(rank: 2, fieldSize),
            precision: 10);
    }

    [Fact]
    public void FinalPositionUsesCurrentPaceSessionWeight()
    {
        var calculator = new DownhillResultScoreCalculator();
        var strongFinal = calculator.Calculate(
            "target",
            [
                Session(PaceSessionType.Final, targetRank: 1),
                Session(PaceSessionType.Qualifying, targetRank: 5)
            ]);
        var strongQualifying = calculator.Calculate(
            "target",
            [
                Session(PaceSessionType.Final, targetRank: 5),
                Session(PaceSessionType.Qualifying, targetRank: 1)
            ]);

        Assert.True(strongFinal.Score > strongQualifying.Score);
        Assert.Equal(2, strongFinal.UsedSessions);
        Assert.Equal(2, strongQualifying.UsedSessions);

        var firstScore = calculator.CalculatePositionScore(rank: 1, fieldSize: 5);
        var lastScore = calculator.CalculatePositionScore(rank: 5, fieldSize: 5);
        Assert.Equal(
            0.85 * firstScore + 0.15 * lastScore,
            strongFinal.Score!.Value,
            precision: 10);
    }

    [Fact]
    public void ZeroWeightTrainingSessionsAreExcluded()
    {
        var calculator = new DownhillResultScoreCalculator();
        var result = calculator.Calculate(
            "target",
            [
                Session(PaceSessionType.Final, targetRank: 2),
                Session(PaceSessionType.TimedTraining, targetRank: 1),
                Session(PaceSessionType.Training, targetRank: 1)
            ]);

        Assert.Equal(
            calculator.CalculatePositionScore(rank: 2, fieldSize: 5),
            result.Score!.Value,
            precision: 10);
        Assert.Equal(1, result.UsedSessions);
    }

    [Fact]
    public void UnclassifiedResultDoesNotAffectScore()
    {
        var calculator = new DownhillResultScoreCalculator();
        var result = calculator.Calculate(
            "target",
            [new PaceSessionData(
                PaceSessionType.Final,
                [
                    Run("target", 1, isClassified: false),
                    Run("rider-2", 1),
                    Run("rider-3", 2)
                ])]);

        Assert.Null(result.Score);
        Assert.Equal(0, result.UsedSessions);
    }

    [Fact]
    public void DnsIsIgnoredWhileDnfContributesZero()
    {
        var calculator = new DownhillResultScoreCalculator();
        var qualifying = Session(PaceSessionType.Qualifying, targetRank: 1);
        var dnsFinal = new PaceSessionData(
            PaceSessionType.Final,
            [
                Run("target", 1, isClassified: false, status: "DNS"),
                Run("rider-1", 1),
                Run("rider-2", 2),
                Run("rider-3", 3),
                Run("rider-4", 4),
                Run("rider-5", 5)
            ]);
        var dnfFinal = dnsFinal with
        {
            Runs = dnsFinal.Runs
                .Select(run => run.RiderId == "target"
                    ? run with { Status = "DNF" }
                    : run)
                .ToArray()
        };

        var withDns = calculator.Calculate("target", [qualifying, dnsFinal]);
        var withDnf = calculator.Calculate("target", [qualifying, dnfFinal]);

        Assert.Equal(100, withDns.Score!.Value, precision: 10);
        Assert.Equal(1, withDns.UsedSessions);
        Assert.True(withDnf.Score < withDns.Score);
        Assert.Equal(2, withDnf.UsedSessions);
    }

    [Fact]
    public void DsqIsIgnoredForRiderAndClassifiedPopulation()
    {
        var calculator = new DownhillResultScoreCalculator();
        var baselineSession = new PaceSessionData(
            PaceSessionType.Final,
            [Run("winner", 1), Run("target", 2), Run("third", 3)]);
        var sessionWithDsq = baselineSession with
        {
            Runs = baselineSession.Runs.Append(
                Run("dsq", 1, status: "DSQ")).ToArray()
        };

        var baseline = calculator.Calculate("target", [baselineSession]);
        var withDsq = calculator.Calculate("target", [sessionWithDsq]);
        var disqualifiedRider = calculator.Calculate("dsq", [sessionWithDsq]);

        Assert.Equal(baseline.Score, withDsq.Score);
        Assert.Null(disqualifiedRider.Score);
        Assert.Equal(0, disqualifiedRider.UsedSessions);
    }

    [Fact]
    public void HigherCompetitionClassHasTwiceTheImpactWhenBothClassesAreActive()
    {
        var calculator = new DownhillResultScoreCalculator();
        var sessions = new[]
        {
            Session(PaceSessionType.Final, targetRank: 1, competitionClassPriority: 1),
            Session(PaceSessionType.Final, targetRank: 5, competitionClassPriority: 2)
        };
        var weighting = CompetitionClassWeighting.FromPriorities([1, 2]);

        var unweighted = calculator.Calculate("target", sessions);
        var weighted = calculator.Calculate("target", sessions, weighting);

        Assert.Equal(50, unweighted.Score!.Value, precision: 10);
        Assert.Equal(200.0 / 3.0, weighted.Score!.Value, precision: 10);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void SingleCompetitionClassDoesNotChangeResultScore(int priority)
    {
        var calculator = new DownhillResultScoreCalculator();
        var sessions = new[]
        {
            Session(
                PaceSessionType.Final,
                targetRank: 1,
                competitionClassPriority: priority),
            Session(
                PaceSessionType.Final,
                targetRank: 5,
                competitionClassPriority: priority)
        };
        var weighting = CompetitionClassWeighting.FromPriorities([priority]);

        var unweighted = calculator.Calculate("target", sessions);
        var weighted = calculator.Calculate("target", sessions, weighting);

        Assert.Equal(unweighted.Score, weighted.Score);
        Assert.Equal(unweighted.Confidence, weighted.Confidence);
    }

    private static PaceSessionData Session(
        PaceSessionType sessionType,
        int targetRank,
        int? competitionClassPriority = null)
    {
        return new PaceSessionData(
            sessionType,
            Enumerable.Range(1, 5)
                .Select(rank => Run(
                    rank == targetRank ? "target" : $"rider-{rank}",
                    rank))
                .ToArray(),
            competitionClassPriority);
    }

    private static PaceRiderRun Run(
        string riderId,
        int rank,
        bool isClassified = true,
        string? status = null)
    {
        return new PaceRiderRun(
            RiderId: riderId,
            SectorTimes: [40.0, 50.0],
            FinishRank: rank,
            FinishTimeSeconds: 200 + rank,
            IsClassified: isClassified,
            Status: status);
    }
}
