using UCI_Stats_API.Services;

namespace UCI_Stats_API.Tests;

public sealed class SectorIncidentDetectionTests
{
    [Fact]
    public void PierronLikeLossRequiresBothTimeAnomalyAndSectorRankCollapse()
    {
        var sectors = PierronSectors();
        var anomalies = SectorIncidentDetector.Analyze(sectors, new PaceOptions());
        Assert.Equal(SectorAnomalyKind.LikelyIncident, anomalies[3].Kind);
        Assert.InRange(anomalies[3].LossSeconds!.Value, 2.53, 2.54);
        Assert.All(anomalies.Where((_, i) => i != 3), a => Assert.Equal(SectorAnomalyKind.Normal, a.Kind));

        // Identical time profile without rank corroboration stays conservative.
        var stableRanks = sectors.Select(s => s with { Rank = 2 }).ToArray();
        Assert.DoesNotContain(SectorIncidentDetector.Analyze(stableRanks, new PaceOptions()),
            a => a.Kind == SectorAnomalyKind.LikelyIncident);
    }

    [Fact]
    public void LargeRankDropWithoutMeaningfulTimeLossIsNotAnIncident()
    {
        var sectors = PierronSectors();
        sectors[3] = sectors[3] with { TimeSeconds = 39.8, Rank = 28 };
        Assert.DoesNotContain(SectorIncidentDetector.Analyze(sectors, new PaceOptions()),
            a => a.Kind == SectorAnomalyKind.LikelyIncident);
    }

    [Theory]
    [InlineData(1.64)]
    [InlineData(1.87)]
    [InlineData(3.11)]
    public void SmallTerminalLossIsNotRescuedByRankCollapse(double loss)
    {
        var sectors = Enumerable.Range(0, 5).Select(i => new PacePrecomputedSector(i,
            i == 4 ? 50 + loss : 50, i == 4 ? 25 : 1,
            new PaceFieldSummary(30, 50, Math.Log(50), .01, .5))).ToArray();
        Assert.DoesNotContain(SectorIncidentDetector.Analyze(sectors, new PaceOptions()),
            a => a.Kind == SectorAnomalyKind.LikelyIncident);
    }

    [Fact]
    public void LargeIsolatedLossDoesNotRequireEliteSectorRanks()
    {
        Assert.Equal(2, Calculate([50, 50, 58.03, 50, 50]).IncidentSectorIndex);
    }

    internal static PacePrecomputedSector[] PierronSectors()
    {
        double[] times = [35.191, 31.446, 39.321, 41.601, 17.805];
        double[] medians = [35.86, 32.545, 41.554, 40.617, 18.5105];
        double[] deviations = [.0126823433, .0176160815, .0305767165, .0267365411, .0223870672];
        int[] ranks = [2, 2, 1, 23, 1];
        return times.Select((t, i) => new PacePrecomputedSector(i, t, ranks[i],
            new PaceFieldSummary(30, medians[i], Math.Log(medians[i]), deviations[i], .5))).ToArray();
    }

    [Fact]
    public void CorrectionsAreAppliedBeforeScoringEveryRiderAndLeaveOfficialRunsUntouched()
    {
        var calculator = new DownhillPaceCalculator();
        var runs = new PaceRiderRun[]
        {
            new("incident", [40, 50, 60, 170], FinishTimeSeconds: 320),
            new("clean", [41, 51, 61, 71], FinishTimeSeconds: 224),
            new("third", [42, 52, 62, 72], FinishTimeSeconds: 228),
            new("fourth", [43, 53, 63, 73], FinishTimeSeconds: 232),
            new("fifth", [44, 54, 64, 74], FinishTimeSeconds: 236)
        };
        var scores = calculator.CalculateSession(new(PaceSessionType.Final, runs)).ToDictionary(r => r.RiderId);
        var corrected = 150 + 73 * (50.0 / 52);
        Assert.Equal(calculator.CalculateFullRunTimeScore(224, [corrected, 224, 228, 232, 236])!.Value,
            scores["clean"].FullRunTimeScore!.Value, 10);
        Assert.Equal(320, runs[0].FinishTimeSeconds);
        Assert.Equal(170, runs[0].SectorTimes[3]);
        var reversed = calculator.CalculateSession(new(PaceSessionType.Final, runs.Reverse().ToArray()));
        foreach (var r in reversed) Assert.Equal(scores[r.RiderId].Pace, r.Pace);
    }

    [Fact]
    public void PartialFieldCannotSilentlyReplaceWholeFieldFinishStatistics()
    {
        var sectors = PierronSectors();
        var s = new PacePrecomputedSessionData("113", "round:13:cat:1", "1", PaceSessionType.Final,
            new("15", sectors.Select(s => s.TimeSeconds).ToArray(), FinishTimeSeconds: 165.364),
            30, sectors, null, null, new(30, 168.905, Math.Log(168.905), .018, 3), []);
        Assert.Null(new DownhillPaceCalculator().PrepareField([s]).Single().PaceFinish);
    }

    [Fact]
    public void CorrectedIsolatedSectorDoesNotCountAsMissingPaceEvidenceButStillReducesConfidence()
    {
        var sectors = PierronSectors();
        var stats = new PaceFieldSummary(30, 168, Math.Log(168), .018, 3);
        var s = new PacePrecomputedSessionData("113", "round:13:cat:1", "1", PaceSessionType.Final,
            new("15", sectors.Select(s => s.TimeSeconds).ToArray(), FinishTimeSeconds: 165.364),
            30, sectors, null, null, stats, []) { PaceFinish = new(162.92, 2.444, stats) };
        var result = new DownhillPaceCalculator().CalculatePrecomputedSession(s);
        Assert.Equal(result.SectorPace, result.EvidenceAdjustedSectorPace);
        Assert.True(result.Confidence < 71);
        Assert.Equal(.8, result.SectorCoverage);
    }

    [Fact]
    public void FastSectorSelectionIsStableAcrossDatabaseFloatingPointNoise()
    {
        WeightedScore[] raw = [new(45, 8), new(45, 2), new(58, 5), new(40, 6), new(59, 4)];
        var transported = raw.Select((s, i) => s with { Score = s.Score + (i == 0 ? -1e-11 : 1e-11) }).ToArray();
        var calculator = new DownhillPaceCalculator();
        Assert.Equal(calculator.CalculateSectorPace(raw), calculator.CalculateSectorPace(transported), 8);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void IsolatedLossIsDetectedEvenForSlowRiderAndOpeningSector(int index)
    {
        double?[] times = [60, 60, 60, 60, 60];
        times[index] = 85;
        var result = Calculate(times);
        Assert.Equal(index, result.IncidentSectorIndex);
        Assert.Null(result.Sectors[index].Score);
        Assert.NotNull(result.Pace);
        Assert.True(result.Confidence < Calculate([60, 60, 60, 60, 60]).Confidence);
    }

    [Fact]
    public void UniformlySlowRiderIsNotAnIncident()
    {
        var result = Calculate([80, 80, 80, 80, 80]);
        Assert.Null(result.IncidentSectorIndex);
        Assert.All(result.Sectors, s => Assert.NotNull(s.Score));
    }

    [Fact]
    public void CompensatingLongShortPairIsNotACrashOrFreePace()
    {
        var result = Calculate([50, 80, 20, 50, 50]);
        Assert.Null(result.IncidentSectorIndex);
        Assert.Null(result.Sectors[1].Score);
        Assert.Null(result.Sectors[2].Score);
    }

    [Fact]
    public void EveryIsolatedIncidentIsExcludedNotOnlyTheFirst()
    {
        var result = Calculate([50, 90, 50, 90, 50, 50, 50]);
        Assert.Equal(PaceSectorStatus.Incident, result.Sectors[1].Status);
        Assert.Equal(PaceSectorStatus.Incident, result.Sectors[3].Status);
    }

    [Fact]
    public void SmallAbsoluteDifferenceInTightlyPackedFieldIsNotAnIncident()
    {
        Assert.Null(Calculate([50, 51, 50, 50, 50]).IncidentSectorIndex);
    }

    [Fact]
    public void InvalidTimingIsNotUsedForConsistencyOutlierProjection()
    {
        var sectors = Calculate([50, 80, 20, 50, 50]).Sectors;
        var profile = new DownhillConsistencyCalculator().AnalyzeSectors(sectors
            .Select(s => s with { Percentile = s.SectorIndex == 1 ? 0 : .9 }).ToArray());
        Assert.Empty(profile.SectorIndexes);
        Assert.Null(profile.ProjectedFinishPositionScore);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(2)]
    public void LargeLossWithoutRecoveryCannotReconstructTheFinish(int firstSlowSector)
    {
        var times = Enumerable.Range(0, 5).Select(i => (double?)(i >= firstSlowSector ? 80 : 50)).ToArray();
        var result = Calculate(times);
        Assert.Equal(firstSlowSector, result.IncidentSectorIndex);
        Assert.False(result.Sectors[firstSlowSector].Anomaly!.AllowsFinishCorrection);
        var stats = new PaceFieldSummary(30, 50, Math.Log(50), .01, .5);
        var field = Enumerable.Range(0, 30).Select(id => new PacePrecomputedSessionData(
            "1", "round:1:category:1", "1", PaceSessionType.Final,
            new(id.ToString(), times, FinishTimeSeconds: times.Sum()), 30,
            times.Select((t, i) => new PacePrecomputedSector(i, t, 20, stats)).ToArray(),
            null, null, stats, [])).ToArray();
        Assert.All(new DownhillPaceCalculator().PrepareField(field), s =>
        {
            Assert.NotNull(s.PaceFinish);
            Assert.Equal(0, s.PaceFinish.RemovedLossSeconds);
            Assert.Equal(s.Run.FinishTimeSeconds, s.PaceFinish.TimeSeconds);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    public void ImpossibleSectorIsInvalidNotACrash(double invalid)
    {
        var result = Calculate([50, invalid, 50, 50, 50]);
        Assert.Null(result.IncidentSectorIndex);
        Assert.Equal(SectorAnomalyKind.InvalidTiming, result.Sectors[1].Anomaly!.Kind);
        Assert.Null(result.Sectors[1].Score);
    }

    [Fact]
    public void TinyFieldDoesNotProduceIncidentEvidence()
    {
        Assert.Null(Calculate([50, 150, 50, 50], fieldSize: 2).IncidentSectorIndex);
    }

    [Fact]
    public void InvalidTimingDoesNotCountAsMissingAttemptInSectorConsistency()
    {
        ConsistencyRunInput Run(string id, double time, bool invalid = false) => new(
            id, "same-course", PaceSessionType.Final,
            [new ConsistencySectorTimeInput("S1", time, IsInvalidTiming: invalid)],
            FinishRank: 2, FieldSize: 30, IsClassified: true);
        var result = new DownhillConsistencyCalculator().Calculate([
            Run("1", 50), Run("2", 50), Run("bad-timer", 20, true)]);
        Assert.Equal(100, result.Sectors.Single().Consistency);
        Assert.Equal(2, result.Sectors.Single().TotalRuns);
        Assert.Equal(100, result.IncidentReliability);
    }

    [Fact]
    public void RecoveryOfVerySlowRiderIsNotAMeasurementError()
    {
        var result = Calculate([200, 280, 120, 200, 200]);
        Assert.DoesNotContain(result.Sectors, s => s.Status == PaceSectorStatus.InvalidTiming);
    }

    internal static RiderSessionPaceResult Calculate(double?[] times, int fieldSize = 30)
    {
        var stats = new PaceFieldSummary(fieldSize, 50, Math.Log(50), .01, .5, 48, 49);
        return new DownhillPaceCalculator().CalculatePrecomputedSession(new(
            "1", "round:1:category:1", "1", PaceSessionType.Final,
            new PaceRiderRun("target", times), fieldSize,
            times.Select((t, i) => new PacePrecomputedSector(i, t, 20, stats)).ToArray(),
            null, null, null, []));
    }
}
