using UCI_Stats_API.Services;

namespace UCI_Stats_API.Tests;

public sealed class DownhillConsistencyCalculatorTests
{
    [Fact]
    public void DefaultOptionsUseNinetyPercentFinishPositionWeight()
    {
        var options = new ConsistencyOptions();

        Assert.Equal(0.90, options.FinishPositionWeight, precision: 10);
        Assert.Equal(0.10, options.SectorConsistencyWeight, precision: 10);
        Assert.Equal(0.06, options.VariationAtHalfScore, precision: 10);
        Assert.Equal(4, options.MaximumProtectedOutlierSectors);
        Assert.Equal(3, options.OneOutlierMaximumLoss, precision: 10);
        Assert.Equal(8, options.TwoOutliersMaximumLoss, precision: 10);
        Assert.Equal(12, options.ThreeOutliersMaximumLoss, precision: 10);
        Assert.Equal(20, options.FourOutliersMaximumLoss, precision: 10);
    }

    [Fact]
    public void SofterVariationCalibrationRewardsTheSameRepeatabilityMore()
    {
        var runs = new[]
        {
            Run("final", PaceSessionType.Final, 30.0),
            Run("qualifying", PaceSessionType.Qualifying, 33.0)
        };
        var previousCalibration = new DownhillConsistencyCalculator(
            new ConsistencyOptions { VariationAtHalfScore = 0.04 });

        var previous = previousCalibration.Calculate(runs);
        var softened = new DownhillConsistencyCalculator().Calculate(runs);

        Assert.True(softened.Consistency > previous.Consistency);
    }

    [Fact]
    public void CompetitionClassWeightingIsUsedOnlyWhenExplicitlyRequested()
    {
        var calculator = new DownhillConsistencyCalculator();
        var runs = new[]
        {
            ResultRun("world-championship", PaceSessionType.Final, 1, 5, 30.0)
                with { CompetitionClassPriority = 1 },
            ResultRun("world-cup", PaceSessionType.Final, 5, 5, 30.0)
                with { CompetitionClassPriority = 2 }
        };

        var unweighted = calculator.Calculate(runs);
        var weighted = calculator.Calculate(
            runs,
            CompetitionClassWeighting.FromPriorities([1, 2]));

        Assert.True(weighted.Consistency > unweighted.Consistency);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void SingleCompetitionClassDoesNotChangeConsistency(int priority)
    {
        var calculator = new DownhillConsistencyCalculator();
        var runs = new[]
        {
            ResultRun("first", PaceSessionType.Final, 1, 5, 30.0)
                with { CompetitionClassPriority = priority },
            ResultRun("second", PaceSessionType.Final, 5, 5, 32.0)
                with { CompetitionClassPriority = priority }
        };

        var unweighted = calculator.Calculate(runs);
        var weighted = calculator.Calculate(
            runs,
            CompetitionClassWeighting.FromPriorities([priority]));

        Assert.Equal(unweighted.Consistency, weighted.Consistency);
        Assert.Equal(unweighted.Confidence, weighted.Confidence);
    }

    [Theory]
    [InlineData(nameof(PaceSessionType.Final), 8.00)]
    [InlineData(nameof(PaceSessionType.SemiFinal), 2.00)]
    [InlineData(nameof(PaceSessionType.Qualifying), 1.50)]
    [InlineData(nameof(PaceSessionType.Seeding), 0.60)]
    [InlineData(nameof(PaceSessionType.TimedTraining), 0.00)]
    [InlineData(nameof(PaceSessionType.Training), 0.00)]
    public void SessionTypeHasExactConsistencyWeight(
        string sessionTypeName,
        double expectedWeight)
    {
        var sessionType = Enum.Parse<PaceSessionType>(sessionTypeName);

        Assert.Equal(
            expectedWeight,
            DownhillConsistencyCalculator.GetConsistencySessionWeight(sessionType),
            precision: 10);
    }

    [Fact]
    public void TrainingDoesNotAffectAnyConsistencyEvidence()
    {
        var calculator = new DownhillConsistencyCalculator();
        var officialRuns = new[]
        {
            Run("final", PaceSessionType.Final, 30.0),
            Run("qualifying", PaceSessionType.Qualifying, 30.5)
        };
        var baseline = calculator.Calculate(officialRuns);
        var withTraining = calculator.Calculate(
            [.. officialRuns, Run("training", PaceSessionType.Training, 90.0)]);

        Assert.Equal(baseline.Consistency, withTraining.Consistency);
        Assert.Equal(baseline.Confidence, withTraining.Confidence);
        Assert.Equal(baseline.UsedRuns, withTraining.UsedRuns);
        Assert.Equal(baseline.UsedSectors, withTraining.UsedSectors);
        Assert.Equal(baseline.Sectors[0], withTraining.Sectors[0]);
    }

    [Fact]
    public void WinningQualifyingAndFinalProducesVeryHighConsistency()
    {
        var result = new DownhillConsistencyCalculator().Calculate(
            [
                ResultRun(
                    "final",
                    PaceSessionType.Final,
                    finishRank: 1,
                    fieldSize: 50,
                    sectorTimes: [90.0]),
                ResultRun(
                    "qualifying",
                    PaceSessionType.Qualifying,
                    finishRank: 1,
                    fieldSize: 50,
                    sectorTimes: [30.0])
            ]);

        var sectorConsistency = result.Sectors.Average(sector => sector.Consistency);
        Assert.Equal(
            0.90 * 100 + 0.10 * sectorConsistency,
            result.Consistency!.Value,
            precision: 10);
        Assert.InRange(result.Consistency.Value, 90, 100);
    }

    [Fact]
    public void FinishPositionDominatesSectorRepeatability()
    {
        var winningWithVariableSectors = new DownhillConsistencyCalculator().Calculate(
            [
                ResultRun("final", PaceSessionType.Final, 1, 50, 90.0),
                ResultRun("qualifying", PaceSessionType.Qualifying, 1, 50, 30.0)
            ]);
        var lastWithIdenticalSectors = new DownhillConsistencyCalculator().Calculate(
            [
                ResultRun("final", PaceSessionType.Final, 50, 50, 30.0),
                ResultRun("qualifying", PaceSessionType.Qualifying, 50, 50, 30.0)
            ]);

        Assert.True(
            winningWithVariableSectors.Consistency >
            lastWithIdenticalSectors.Consistency);
        Assert.Equal(10, lastWithIdenticalSectors.Consistency!.Value, precision: 10);
    }

    [Fact]
    public void StrongFinalPositionMattersMoreThanStrongQualifyingPosition()
    {
        var strongFinal = new DownhillConsistencyCalculator().Calculate(
            [
                ResultRun("final", PaceSessionType.Final, 1, 50, 30.0),
                ResultRun("qualifying", PaceSessionType.Qualifying, 50, 50, 30.0)
            ]);
        var strongQualifying = new DownhillConsistencyCalculator().Calculate(
            [
                ResultRun("final", PaceSessionType.Final, 50, 50, 30.0),
                ResultRun("qualifying", PaceSessionType.Qualifying, 1, 50, 30.0)
            ]);

        Assert.True(strongFinal.Consistency > strongQualifying.Consistency);
    }

    [Fact]
    public void UnclassifiedFinalReceivesZeroFinishPositionScore()
    {
        var completedFinal = new DownhillConsistencyCalculator().Calculate(
            [
                ResultRun("final", PaceSessionType.Final, 1, 50, 30.0),
                ResultRun("qualifying", PaceSessionType.Qualifying, 1, 50, 30.0)
            ]);
        var finalDnf = new DownhillConsistencyCalculator().Calculate(
            [
                ResultRun(
                    "final",
                    PaceSessionType.Final,
                    finishRank: null,
                    fieldSize: 50,
                    isClassified: false,
                    sectorTimes: [(double?)null]),
                ResultRun("qualifying", PaceSessionType.Qualifying, 1, 50, 30.0)
            ]);

        Assert.Equal(100, completedFinal.Consistency!.Value, precision: 10);
        Assert.True(finalDnf.Consistency < 20);
    }

    [Fact]
    public void MissingTrainingIsNotTreatedAsDnf()
    {
        var calculator = new DownhillConsistencyCalculator();
        var baseline = calculator.Calculate(
            [
                Run("final", PaceSessionType.Final, 30.0),
                Run("qualifying", PaceSessionType.Qualifying, 30.0)
            ]);
        var withMissingTraining = calculator.Calculate(
            [
                Run("final", PaceSessionType.Final, 30.0),
                Run("qualifying", PaceSessionType.Qualifying, 30.0),
                Run("training", PaceSessionType.Training, (double?)null)
            ]);

        Assert.Equal(100, baseline.Consistency!.Value, precision: 10);
        Assert.Equal(baseline.Consistency, withMissingTraining.Consistency);
        Assert.Equal(baseline.Confidence, withMissingTraining.Confidence);
        Assert.Equal(baseline.UsedRuns, withMissingTraining.UsedRuns);
        Assert.Equal(baseline.Sectors[0], withMissingTraining.Sectors[0]);
    }

    [Fact]
    public void DnsIsIgnoredAndDnfStillLowersConsistency()
    {
        var calculator = new DownhillConsistencyCalculator();
        var completedRuns = new[]
        {
            ResultRun("qualifying", PaceSessionType.Qualifying, 1, 50, 30.0),
            ResultRun("semi", PaceSessionType.SemiFinal, 1, 50, 30.0)
        };
        var missingFinal = ResultRun(
            "final",
            PaceSessionType.Final,
            finishRank: null,
            fieldSize: 50,
            isClassified: false,
            sectorTimes: [(double?)null]);

        var baseline = calculator.Calculate(completedRuns);
        var withDns = calculator.Calculate(
            [.. completedRuns, missingFinal with { Status = "DNS" }]);
        var withDnf = calculator.Calculate(
            [.. completedRuns, missingFinal with { Status = "DNF" }]);

        Assert.Equal(baseline.Consistency, withDns.Consistency);
        Assert.Equal(baseline.UsedRuns, withDns.UsedRuns);
        Assert.True(withDnf.Consistency < baseline.Consistency);
        Assert.Equal(3, withDnf.UsedRuns);
    }

    [Fact]
    public void HigherWeightSlowFinalAffectsVariationMoreThanSlowTimedTraining()
    {
        var calculator = new DownhillConsistencyCalculator();
        var slowFinal = calculator.Calculate(
            [
                Run("final", PaceSessionType.Final, 35.0),
                Run("qualifying", PaceSessionType.Qualifying, 30.0),
                Run("timed", PaceSessionType.TimedTraining, 30.0)
            ]);
        var slowTimedTraining = calculator.Calculate(
            [
                Run("final", PaceSessionType.Final, 30.0),
                Run("qualifying", PaceSessionType.Qualifying, 30.0),
                Run("timed", PaceSessionType.TimedTraining, 35.0)
            ]);

        Assert.True(slowFinal.Consistency < slowTimedTraining.Consistency);
    }

    [Fact]
    public void SlowFinalLowersConsistencyMoreThanEquallySlowQualifying()
    {
        var calculator = new DownhillConsistencyCalculator();
        var slowFinal = calculator.Calculate(
            [
                Run("final", PaceSessionType.Final, 40.0),
                Run("semi", PaceSessionType.SemiFinal, 30.0),
                Run("qualifying", PaceSessionType.Qualifying, 30.0)
            ]);
        var slowQualifying = calculator.Calculate(
            [
                Run("final", PaceSessionType.Final, 30.0),
                Run("semi", PaceSessionType.SemiFinal, 30.0),
                Run("qualifying", PaceSessionType.Qualifying, 40.0)
            ]);

        Assert.True(slowFinal.Consistency < slowQualifying.Consistency);
    }

    [Fact]
    public void RecordedCrashTimeLowersConsistency()
    {
        var calculator = new DownhillConsistencyCalculator();
        var clean = calculator.Calculate(
            [
                Run("final", PaceSessionType.Final, 30.0),
                Run("qualifying", PaceSessionType.Qualifying, 30.2),
                Run("semi", PaceSessionType.SemiFinal, 30.1)
            ]);
        var crash = calculator.Calculate(
            [
                Run("final", PaceSessionType.Final, 90.0),
                Run("qualifying", PaceSessionType.Qualifying, 30.2),
                Run("semi", PaceSessionType.SemiFinal, 30.1)
            ]);

        Assert.True(crash.Consistency < clean.Consistency);
        Assert.True(crash.Sectors[0].RelativeVariation > clean.Sectors[0].RelativeVariation);
    }

    [Fact]
    public void VerySlowTimeIsNotRemovedAsOutlier()
    {
        var result = new DownhillConsistencyCalculator().Calculate(
            [
                Run("final", PaceSessionType.Final, 300.0),
                Run("qualifying", PaceSessionType.Qualifying, 30.0),
                Run("semi", PaceSessionType.SemiFinal, 30.0)
            ]);

        Assert.Equal(3, result.Sectors[0].CompletedRuns);
        Assert.True(result.Sectors[0].RelativeVariation > 0.5);
        Assert.True(result.Consistency < 1);
    }

    [Theory]
    [InlineData(1, 97)]
    [InlineData(2, 92)]
    [InlineData(3, 88)]
    [InlineData(4, 80)]
    public void OutlierSectorCountUsesProgressiveMaximumLoss(
        int outlierCount,
        double expectedConsistency)
    {
        var sectorCount = Math.Max(4, outlierCount + 1);
        var outlierIndexes = Enumerable.Range(0, outlierCount).ToHashSet();
        var qualifyingTimes = Enumerable.Repeat<double?>(30, sectorCount).ToArray();
        var finalTimes = Enumerable.Range(0, sectorCount)
            .Select(index => (double?)(outlierIndexes.Contains(index) ? 300 : 30))
            .ToArray();
        var result = new DownhillConsistencyCalculator().Calculate(
            [
                ProtectedResultRun(
                    "qualifying",
                    PaceSessionType.Qualifying,
                    finishRank: 1,
                    fieldSize: 80,
                    projectedFinishPositionScore: null,
                    outlierIndexes: new HashSet<int>(),
                    qualifyingTimes),
                ProtectedResultRun(
                    "final",
                    PaceSessionType.Final,
                    finishRank: 80,
                    fieldSize: 80,
                    projectedFinishPositionScore: 100,
                    outlierIndexes,
                    finalTimes)
            ]);

        Assert.Equal(
            expectedConsistency,
            result.Consistency!.Value,
            precision: 10);
    }

    [Fact]
    public void MoreThanFourOutlierSectorsDoesNotReceiveProtection()
    {
        var outlierIndexes = Enumerable.Range(0, 5).ToHashSet();
        var result = new DownhillConsistencyCalculator().Calculate(
            [
                ProtectedResultRun(
                    "qualifying",
                    PaceSessionType.Qualifying,
                    1,
                    80,
                    null,
                    new HashSet<int>(),
                    30, 30, 30, 30, 30, 30),
                ProtectedResultRun(
                    "final",
                    PaceSessionType.Final,
                    80,
                    80,
                    100,
                    outlierIndexes,
                    300, 300, 300, 300, 300, 30)
            ]);

        Assert.True(result.Consistency < 50);
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(2, 4)]
    [InlineData(3, 5)]
    [InlineData(4, 6)]
    public void SectorAnalysisFindsSmallGroupsOfLargePercentileDrops(
        int expectedOutliers,
        int sectorCount)
    {
        var percentiles = Enumerable.Range(0, sectorCount)
            .Select(index => index < sectorCount - expectedOutliers
                ? 0.95
                : 0.05)
            .ToArray();
        var sectors = percentiles
            .Select((percentile, index) => new PaceSectorResult(
                index,
                PaceSectorStatus.Valid,
                Time: 30,
                Rank: null,
                Percentile: percentile,
                PerformanceZ: null,
                Score: 100 * percentile))
            .ToArray();

        var profile = new DownhillConsistencyCalculator()
            .AnalyzeSectors(sectors);

        Assert.Equal(expectedOutliers, profile.SectorIndexes.Count);
        Assert.Equal(95, profile.ProjectedFinishPositionScore);
    }

    [Fact]
    public void FinalDnfPenalizesMoreThanTimedTrainingDnf()
    {
        var calculator = new DownhillConsistencyCalculator();
        var finalDnf = calculator.Calculate(
            [
                Run("qualifying", PaceSessionType.Qualifying, 30.0),
                Run("semi", PaceSessionType.SemiFinal, 30.0),
                Run("final", PaceSessionType.Final, (double?)null)
            ]);
        var timedTrainingDnf = calculator.Calculate(
            [
                Run("qualifying", PaceSessionType.Qualifying, 30.0),
                Run("semi", PaceSessionType.SemiFinal, 30.0),
                Run("timed", PaceSessionType.TimedTraining, (double?)null)
            ]);

        Assert.True(finalDnf.Consistency < timedTrainingDnf.Consistency);
    }

    [Fact]
    public void DnfOnlyChangesWeightedCompletionRate()
    {
        var result = new DownhillConsistencyCalculator().Calculate(
            [
                Run("qualifying", PaceSessionType.Qualifying, 30.0),
                Run("semi", PaceSessionType.SemiFinal, 30.0),
                Run("final", PaceSessionType.Final, (double?)null)
            ]);
        var expectedCompletionRate = (1.50 + 2.00) / (1.50 + 2.00 + 8.00);

        Assert.Equal(0, result.Sectors[0].RelativeVariation!.Value, precision: 10);
        Assert.Equal(100 * expectedCompletionRate, result.Consistency!.Value, precision: 10);
    }

    [Fact]
    public void CalculatorUsesOnlyProvidedRiderRunsAndNoFieldData()
    {
        var result = new DownhillConsistencyCalculator().Calculate(
            [
                Run("rider-final", PaceSessionType.Final, 30.0),
                Run("rider-qualifying", PaceSessionType.Qualifying, 30.3)
            ]);

        Assert.NotNull(result.Consistency);
        Assert.Equal(2, result.UsedRuns);
        Assert.Single(result.Sectors);
    }

    [Fact]
    public void FewerThanTwoRelevantRunsReturnsNoConsistency()
    {
        var result = new DownhillConsistencyCalculator().Calculate(
            [
                Run("final", PaceSessionType.Final, 30.0),
                Run("training", PaceSessionType.Training, 31.0)
            ]);

        Assert.Null(result.Consistency);
        Assert.Equal(0, result.Confidence);
        Assert.Equal(1, result.UsedRuns);
        Assert.Equal(0, result.UsedSectors);
    }

    [Fact]
    public void RunsOnDifferentCourseVersionsAreNotCompared()
    {
        var result = new DownhillConsistencyCalculator().Calculate(
            [
                RunOnCourse("course-a", PaceSessionType.Final, "course-a", 30.0),
                RunOnCourse("course-b", PaceSessionType.Qualifying, "course-b", 30.0)
            ]);

        Assert.Null(result.Consistency);
        Assert.Equal(0, result.Confidence);
    }

    [Fact]
    public void EverySectorHasEqualWeightInFinalResult()
    {
        var result = new DownhillConsistencyCalculator().Calculate(
            [
                Run("final", PaceSessionType.Final, 30.0, 40.0),
                Run("qualifying", PaceSessionType.Qualifying, 30.0, 60.0)
            ]);

        Assert.Equal(2, result.Sectors.Count);
        Assert.Equal(
            result.Sectors.Average(sector => sector.Consistency),
            result.Consistency!.Value,
            precision: 10);
    }

    [Fact]
    public void IdenticalTimesInAllOfficialSessionsGivePerfectConsistency()
    {
        var result = new DownhillConsistencyCalculator().Calculate(
            [
                Run("final", PaceSessionType.Final, 30.0),
                Run("semi", PaceSessionType.SemiFinal, 30.0),
                Run("qualifying", PaceSessionType.Qualifying, 30.0),
                Run("seeding", PaceSessionType.Seeding, 30.0),
                Run("timed", PaceSessionType.TimedTraining, 30.0)
            ]);

        Assert.Equal(0, result.Sectors[0].RelativeVariation!.Value, precision: 10);
        Assert.Equal(100, result.Consistency!.Value, precision: 10);
    }

    [Fact]
    public void ResultAlwaysRemainsWithinZeroToOneHundred()
    {
        var result = new DownhillConsistencyCalculator().Calculate(
            [
                Run("final", PaceSessionType.Final, 0.01),
                Run("qualifying", PaceSessionType.Qualifying, 1_000_000_000.0),
                Run("semi", PaceSessionType.SemiFinal, (double?)null)
            ]);

        Assert.InRange(result.Consistency!.Value, 0, 100);
        Assert.InRange(result.Sectors[0].Consistency, 0, 100);
    }

    private static ConsistencyRunInput Run(
        string runId,
        PaceSessionType sessionType,
        params double?[] sectorTimes)
    {
        return RunOnCourse(runId, sessionType, "course", sectorTimes);
    }

    private static ConsistencyRunInput RunOnCourse(
        string runId,
        PaceSessionType sessionType,
        string courseKey,
        params double?[] sectorTimes)
    {
        return new ConsistencyRunInput(
            runId,
            courseKey,
            sessionType,
            sectorTimes
                .Select((time, index) => new ConsistencySectorTimeInput(
                    $"sector:{index}",
                    time))
                .ToArray());
    }

    private static ConsistencyRunInput ResultRun(
        string runId,
        PaceSessionType sessionType,
        int? finishRank,
        int fieldSize,
        params double?[] sectorTimes)
    {
        return ResultRun(
            runId,
            sessionType,
            finishRank,
            fieldSize,
            isClassified: finishRank.HasValue,
            sectorTimes: sectorTimes);
    }

    private static ConsistencyRunInput ResultRun(
        string runId,
        PaceSessionType sessionType,
        int? finishRank,
        int fieldSize,
        bool isClassified,
        params double?[] sectorTimes)
    {
        return new ConsistencyRunInput(
            runId,
            "course",
            sessionType,
            sectorTimes
                .Select((time, index) => new ConsistencySectorTimeInput(
                    $"sector:{index}",
                    time))
                .ToArray(),
            finishRank,
            fieldSize,
            isClassified);
    }

    private static ConsistencyRunInput ProtectedResultRun(
        string runId,
        PaceSessionType sessionType,
        int? finishRank,
        int fieldSize,
        double? projectedFinishPositionScore,
        IReadOnlySet<int> outlierIndexes,
        params double?[] sectorTimes)
    {
        return new ConsistencyRunInput(
            runId,
            "course",
            sessionType,
            sectorTimes
                .Select((time, index) => new ConsistencySectorTimeInput(
                    $"sector:{index}",
                    time,
                    outlierIndexes.Contains(index)))
                .ToArray(),
            finishRank,
            fieldSize,
            finishRank.HasValue,
            CompetitionClassPriority: null,
            ProjectedFinishPositionScore:
                projectedFinishPositionScore);
    }
}
