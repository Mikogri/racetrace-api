using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class ResultScorePreparationTests
{
    [Fact]
    public void RawIndexedAndPrecomputedPathsAgreeForValidFields()
    {
        var calculator = new DownhillResultScoreCalculator();
        PaceSessionData[] sessions = [
            new(PaceSessionType.Final, [new("target", [], FinishRank: 2), new("first", [], FinishRank: 1), new("last", [], FinishRank: 3)], 1),
            new(PaceSessionType.Qualifying1, [new("target", [], FinishRank: 1), new("second", [], FinishRank: 2), new("last", [], FinishRank: 3)], 2)];
        var weighting = CompetitionClassWeighting.FromPriorities([1, 2]);
        var precomputed = sessions.Select(s => new PacePrecomputedSessionData("session", "course", null,
            s.SessionType, s.Runs[0], 3, [], null, null, null, [], s.CompetitionClassPriority)).ToArray();

        var expected = calculator.Calculate("target", sessions, weighting);
        Assert.Equal(expected, calculator.Calculate(precomputed, weighting));
        Assert.Equal(expected, calculator.CalculatePrepared("target", sessions.Select(DownhillResultScoreCalculator.PrepareSession), weighting));
    }

    [Fact]
    public void DuplicateRiderUsesFirstStatusAndFirstEligibleRankIgnoringCase()
    {
        var calculator = new DownhillResultScoreCalculator();
        var session = new PaceSessionData(PaceSessionType.Final, [
            new("target", [], IsClassified: false),
            new("TARGET", [], FinishRank: 2),
            new("target", [], FinishRank: 3),
            new("first", [], FinishRank: 1),
            new("third", [], FinishRank: 3)]);

        Assert.Equal(calculator.CalculatePositionScore(2, 3), calculator.Calculate("target", [session]).Score);
        var ignored = session with { Runs = [session.Runs[0] with { Status = " DNS " }, .. session.Runs.Skip(1)] };
        Assert.Null(calculator.Calculate("target", [ignored]).Score);
    }

    [Fact]
    public void ZeroEffectiveWeightStillCountsAsEvidenceAndUsedSession()
    {
        var calculator = new DownhillResultScoreCalculator(sessionWeights: new() { FinalTargetImpact = 1 });
        PaceRiderRun[] runs = [new("target", [], FinishRank: 1), new("other", [], FinishRank: 2)];
        var final = new PaceSessionData(PaceSessionType.Final, runs);
        var qualifying = final with { SessionType = PaceSessionType.Qualifying1 };

        var finalOnly = calculator.Calculate("target", [final]);
        var combined = calculator.Calculate("target", [final, qualifying]);
        Assert.Equal(finalOnly.Score, combined.Score);
        Assert.Equal(2, combined.UsedSessions);
        Assert.True(combined.Confidence > finalOnly.Confidence);
    }
}
