using RaceTrace.Api.Services;
using System.Text.Json;

namespace RaceTrace.Api.Tests;

public sealed class PacePreparationTests
{
    [Fact]
    public void PreparedCumulativeTimeMatchesIndependentIncidentCalculation()
    {
        var runs = new List<PaceRiderRun>
        {
            new("target", [50, 50, 80, 50, 50], IsClassified: false)
        };
        runs.AddRange(Enumerable.Range(1, 6).Select(index =>
            new PaceRiderRun(index.ToString(), [50, 50, 50, 50, 50],
                FinishTimeSeconds: 250)));
        var calculator = new DownhillPaceCalculator();
        var prepared = calculator.PrepareSession(new(PaceSessionType.Final, runs))[0];
        var withPreparedTime = calculator.CalculatePrecomputedSession(prepared);
        var independent = calculator.CalculatePrecomputedSession(prepared with
        {
            PreparedCumulativeTimes = null
        });

        Assert.Equal(2, withPreparedTime.IncidentSectorIndex);
        Assert.Equal(independent.FullRunTimeScore, withPreparedTime.FullRunTimeScore);
        Assert.Equal(independent.Pace, withPreparedTime.Pace);
    }

    [Fact]
    public void FractionalCumulativeTimesKeepLeftToRightPrecision()
    {
        var session = new PaceSessionData(PaceSessionType.Final, [new("rider", [.1, .2, .3])]);
        var checkpoints = new DownhillPaceCalculator().PrepareSession(session)[0].CumulativeStatistics;

        Assert.Equal(new[] { .1, .30000000000000004, .6000000000000001 },
            checkpoints.Select(s => s!.Median));
    }

    [Fact]
    public void PrecomputedSectorOrderDoesNotChangeTheResultOrMutateInput()
    {
        var runs = Enumerable.Range(0, 8).Select(i => new PaceRiderRun(i.ToString(),
            [30 + i * .1, 40 + i * .2, 20 + i * .3], 50 + i, i + 1, 90 + i * .6)).ToArray();
        var calculator = new DownhillPaceCalculator();
        var session = calculator.PrepareSession(new(PaceSessionType.Final, runs))[0];
        var reversed = session with { Sectors = session.Sectors.Reverse().ToArray() };

        Assert.Equal(JsonSerializer.Serialize(calculator.CalculatePrecomputedSession(session)),
            JsonSerializer.Serialize(calculator.CalculatePrecomputedSession(reversed)));
        Assert.Equal(new[] { 2, 1, 0 }, reversed.Sectors.Select(s => s.SectorIndex));
    }

    [Fact]
    public void EqualTimesAndSpeedsShareCompetitionRanks()
    {
        var runs = new[] { 10.0, 10, 20, 30, 30 }
            .Select((value, index) => new PaceRiderRun(index.ToString(), [value], TopSpeed: value)).ToArray();
        var prepared = new DownhillPaceCalculator().PrepareSession(new(PaceSessionType.Final, runs));

        Assert.Equal(new int?[] { 1, 1, 3, 4, 4 }, prepared.Select(s => s.Sectors[0].Rank));
        Assert.Equal(new int?[] { 4, 4, 3, 1, 1 }, prepared.Select(s => s.SpeedRank));
    }

    [Fact]
    public void MissingOrInvalidCheckpointInvalidatesOnlyItsCumulativeSuffix()
    {
        PaceRiderRun[] runs =
        [
            new("complete", [10, 20, 30]),
            new("missing", [10, null, 30]),
            new("zero", [10, 0, 30]),
            new("negative", [10, -1, 30]),
            new("nan", [10, double.NaN, 30]),
            new("infinite", [10, double.PositiveInfinity, 30]),
            new("short", [10]),
            new("empty", [])
        ];
        var checkpoints = new DownhillPaceCalculator().PrepareSession(new(PaceSessionType.Final, runs))[0].CumulativeStatistics;

        Assert.Equal(new[] { 7, 1, 1 }, checkpoints.Select(s => s!.Count));
        Assert.Equal(new[] { 10.0, 30, 60 }, checkpoints.Select(s => s!.Median));
    }
}
