using UCI_Stats_API.Services;

namespace UCI_Stats_API.Tests;

public sealed class DownhillPaceCalculatorTests
{
    [Fact]
    public void PrecomputedFieldStatisticsMatchRawSessionCalculation()
    {
        var calculator = new DownhillPaceCalculator();
        var runs = new[]
        {
            Run("target", [42, 52], topSpeed: 58, finishRank: 3, finishTimeSeconds: 94),
            Run("rider-1", [40, 50], topSpeed: 60, finishRank: 1, finishTimeSeconds: 90),
            Run("rider-2", [41, 51], topSpeed: 59, finishRank: 2, finishTimeSeconds: 92),
            Run("rider-3", [43, 53], topSpeed: 57, finishRank: 4, finishTimeSeconds: 96),
            Run("rider-4", [44, 54], topSpeed: 56, finishRank: 5, finishTimeSeconds: 98)
        };
        var raw = Find(calculator.CalculateSession(new PaceSessionData(
            PaceSessionType.Final,
            runs)), "target");
        var precomputed = calculator.CalculatePrecomputedSession(new PacePrecomputedSessionData(
            SessionId: "10",
            CourseKey: "round:1:category:1",
            CategoryId: "1",
            SessionType: PaceSessionType.Final,
            Run: runs[0],
            ClassifiedFieldSize: 5,
            Sectors:
            [
                new PacePrecomputedSector(0, 42, 3, Summary(40, 41, 42, 43, 44)),
                new PacePrecomputedSector(1, 52, 3, Summary(50, 51, 52, 53, 54))
            ],
            SpeedStatistics: Summary(56, 57, 58, 59, 60),
            SpeedRank: 3,
            FinishStatistics: Summary(90, 92, 94, 96, 98),
            CumulativeStatistics:
            [
                Summary(40, 41, 42, 43, 44),
                Summary(90, 92, 94, 96, 98)
            ]));

        Assert.Equal(raw.Pace!.Value, precomputed.Pace!.Value, precision: 10);
        Assert.Equal(raw.SectorPace!.Value, precomputed.SectorPace!.Value, precision: 10);
        Assert.Equal(raw.SpeedScore!.Value, precomputed.SpeedScore!.Value, precision: 10);
        Assert.Equal(raw.FullRunTimeScore!.Value, precomputed.FullRunTimeScore!.Value, precision: 10);
        Assert.Equal(raw.Confidence, precomputed.Confidence, precision: 10);
        Assert.Equal(raw.Sectors, precomputed.Sectors);
    }

    [Fact]
    public void StrongSectorCalibrationRaisesOnlyScoresAboveSeventyFive()
    {
        var calculator = new DownhillPaceCalculator();

        Assert.Equal(75, calculator.ApplyPositiveSectorCalibration(75), precision: 10);
        Assert.InRange(calculator.ApplyPositiveSectorCalibration(85), 88, 90);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(60)]
    [InlineData(70)]
    [InlineData(75)]
    public void SectorScoresAtOrBelowSeventyFiveAreNotRaised(double rawScore)
    {
        var calibrated = new DownhillPaceCalculator()
            .ApplyPositiveSectorCalibration(rawScore);

        Assert.Equal(rawScore, calibrated, precision: 10);
    }

    [Fact]
    public void ConfiguredFinalWinBonusDoesNotChangePace()
    {
        var session = new PaceSessionData(
            PaceSessionType.Final,
            [
                Run("target", [43, 53], finishRank: 1),
                Run("rider-2", [40, 50], finishRank: 2),
                Run("rider-3", [41, 51], finishRank: 3),
                Run("rider-4", [42, 52], finishRank: 4),
                Run("rider-5", [44, 54], finishRank: 5)
            ]);
        var withoutBonus = Find(new DownhillPaceCalculator(new PaceOptions
        {
            FinalWinBonus = 0
        }).CalculateSession(session), "target");
        var withConfiguredBonus = Find(new DownhillPaceCalculator(new PaceOptions
        {
            FinalWinBonus = 100
        }).CalculateSession(session), "target");
        var changedFinishRank = Find(new DownhillPaceCalculator().CalculateSession(new PaceSessionData(
            PaceSessionType.Final,
            [
                Run("target", [43, 53], finishRank: 5),
                Run("rider-2", [40, 50], finishRank: 1),
                Run("rider-3", [41, 51], finishRank: 2),
                Run("rider-4", [42, 52], finishRank: 3),
                Run("rider-5", [44, 54], finishRank: 4)
            ])), "target");

        Assert.Equal(withoutBonus.Pace, withConfiguredBonus.Pace);
        Assert.Equal(withoutBonus.Pace, changedFinishRank.Pace);
    }

    [Fact]
    public void DominantFullRunReceivesHighTimeScoreWithoutUsingFinishRank()
    {
        var calculator = new DownhillPaceCalculator();
        var session = new PaceSessionData(
            PaceSessionType.Final,
            [
                Run("target", [42, 52], finishRank: 5, finishTimeSeconds: 240.0),
                Run("rider-2", [40, 50], finishRank: 2, finishTimeSeconds: 243.7),
                Run("rider-3", [41, 51], finishRank: 3, finishTimeSeconds: 245.0),
                Run("rider-4", [43, 53], finishRank: 4, finishTimeSeconds: 246.0),
                Run("rider-5", [44, 54], finishRank: 1, finishTimeSeconds: 247.0)
            ]);

        var result = Find(calculator.CalculateSession(session), "target");

        Assert.True(result.FullRunTimeScore > 95);
        Assert.Equal(
            DownhillPaceCalculator.CombineSectorAndFullRunPace(
                PaceSessionType.Final,
                result.EvidenceAdjustedSectorPace!.Value,
                result.FullRunTimeScore) + result.DominanceBonus,
            result.Pace!.Value,
            precision: 10);
        Assert.InRange(result.DominanceBonus, 0.01, 3.0);
    }

    [Fact]
    public void FullRunTimeScoreIncreasesWithLargerTimeAdvantage()
    {
        var calculator = new DownhillPaceCalculator();
        var field = new[] { 240.0, 246.5, 247.0, 248.0, 249.0 };

        var dominant = calculator.CalculateFullRunTimeScore(240.0, field);
        var nearMedian = calculator.CalculateFullRunTimeScore(247.0, field);

        Assert.True(dominant > nearMedian);
        Assert.True(dominant > 95);
    }

    [Fact]
    public void ChangingFinishRankWithIdenticalTimesDoesNotChangePace()
    {
        var calculator = new DownhillPaceCalculator();
        var first = new PaceSessionData(
            PaceSessionType.Final,
            [
                Run("target", [42, 52], finishRank: 1, finishTimeSeconds: 240.0),
                Run("rider-2", [40, 50], finishRank: 2, finishTimeSeconds: 246.5),
                Run("rider-3", [41, 51], finishRank: 3, finishTimeSeconds: 247.0),
                Run("rider-4", [43, 53], finishRank: 4, finishTimeSeconds: 248.0),
                Run("rider-5", [44, 54], finishRank: 5, finishTimeSeconds: 249.0)
            ]);
        var changedRanks = first with
        {
            Runs = first.Runs
                .Select((run, index) => run with { FinishRank = 5 - index })
                .ToArray()
        };

        var original = Find(calculator.CalculateSession(first), "target");
        var changed = Find(calculator.CalculateSession(changedRanks), "target");

        Assert.Equal(original.FullRunTimeScore, changed.FullRunTimeScore);
        Assert.Equal(original.Pace, changed.Pace);
    }

    [Fact]
    public void WeakerValidSectorStillLowersSectorPace()
    {
        var strongSession = MultiSectorField([40, 50, 60, 70, 80]);
        var weakerSession = MultiSectorField([45, 50, 60, 70, 80]);
        var calculator = new DownhillPaceCalculator();
        var strong = Find(calculator.CalculateSession(strongSession), "target");
        var weaker = Find(calculator.CalculateSession(weakerSession), "target");

        Assert.Equal(PaceSectorStatus.Valid, weaker.Sectors[0].Status);
        Assert.True(weaker.SectorPace < strong.SectorPace);
    }

    [Fact]
    public void StrongRepresentativeSectorAfterIncidentIsIncluded()
    {
        var session = new PaceSessionData(
            PaceSessionType.Final,
            [
                Run("target", [40, 50, 120, 70], finishRank: 5),
                Run("rider-2", [41, 51, 61, 71], finishRank: 1),
                Run("rider-3", [42, 52, 62, 72], finishRank: 2),
                Run("rider-4", [43, 53, 63, 73], finishRank: 3),
                Run("rider-5", [44, 54, 64, 74], finishRank: 4)
            ]);
        var result = Find(new DownhillPaceCalculator().CalculateSession(session), "target");

        Assert.Equal(2, result.IncidentSectorIndex);
        Assert.Equal(PaceSectorStatus.Incident, result.Sectors[2].Status);
        Assert.Null(result.Sectors[2].Score);
        Assert.Equal(PaceSectorStatus.RecoveredAfterIncident, result.Sectors[3].Status);
        Assert.NotNull(result.Sectors[3].Score);
        Assert.Equal(0.75, result.SectorCoverage, precision: 10);
    }

    [Fact]
    public void PostIncidentFlagFalseFiltersEveryFollowingSector()
    {
        var session = new PaceSessionData(
            PaceSessionType.Final,
            [
                Run("target", [40, 50, 120, 70], finishRank: 5),
                Run("rider-2", [41, 51, 61, 71], finishRank: 1),
                Run("rider-3", [42, 52, 62, 72], finishRank: 2),
                Run("rider-4", [43, 53, 63, 73], finishRank: 3),
                Run("rider-5", [44, 54, 64, 74], finishRank: 4)
            ]);
        var calculator = new DownhillPaceCalculator(new PaceOptions
        {
            CountPositiveSectorsAfterIncident = false
        });

        var result = Find(calculator.CalculateSession(session), "target");

        Assert.Equal(2, result.IncidentSectorIndex);
        Assert.Equal(PaceSectorStatus.AfterIncident, result.Sectors[3].Status);
        Assert.Null(result.Sectors[3].Score);
        Assert.Equal(0.50, result.SectorCoverage, precision: 10);
    }

    [Fact]
    public void WeakSectorAfterIncidentIsFilteredButLaterRecoveryIsIncluded()
    {
        var session = new PaceSessionData(
            PaceSessionType.Final,
            [
                Run("target", [40, 50, 120, 90, 80], finishRank: 5),
                Run("rider-2", [41, 51, 61, 71, 81], finishRank: 1),
                Run("rider-3", [42, 52, 62, 72, 82], finishRank: 2),
                Run("rider-4", [43, 53, 63, 73, 83], finishRank: 3),
                Run("rider-5", [44, 54, 64, 74, 84], finishRank: 4)
            ]);
        var result = Find(new DownhillPaceCalculator().CalculateSession(session), "target");

        Assert.Equal(2, result.IncidentSectorIndex);
        Assert.Equal(PaceSectorStatus.AfterIncident, result.Sectors[3].Status);
        Assert.Null(result.Sectors[3].Score);
        Assert.Equal(PaceSectorStatus.RecoveredAfterIncident, result.Sectors[4].Status);
        Assert.NotNull(result.Sectors[4].Score);
        Assert.Equal(0.60, result.SectorCoverage, precision: 10);
    }

    [Fact]
    public void CompetitiveSectorsAfterEarlyIncidentRestorePaceWithoutOneSplitTimeScore()
    {
        var session = new PaceSessionData(
            PaceSessionType.Final,
            [
                Run("target", [40, 120, 62, 72, 82], finishRank: 20, finishTimeSeconds: 316),
                Run("rider-2", [41, 51, 61, 71, 81], finishRank: 1, finishTimeSeconds: 205),
                Run("rider-3", [42, 52, 63, 73, 83], finishRank: 2, finishTimeSeconds: 210),
                Run("rider-4", [43, 53, 64, 74, 84], finishRank: 3, finishTimeSeconds: 215),
                Run("rider-5", [44, 54, 65, 75, 85], finishRank: 4, finishTimeSeconds: 220)
            ]);

        var result = Find(new DownhillPaceCalculator().CalculateSession(session), "target");

        Assert.Equal(1, result.IncidentSectorIndex);
        Assert.Equal(PaceSectorStatus.Incident, result.Sectors[1].Status);
        Assert.All(result.Sectors.Skip(2), sector =>
            Assert.Equal(PaceSectorStatus.RecoveredAfterIncident, sector.Status));
        Assert.NotNull(result.Pace);
        Assert.Null(result.FullRunTimeScore);
        Assert.Equal(0.80, result.SectorCoverage, precision: 10);
    }

    [Fact]
    public void IncidentUsesLastCleanCheckpointInsteadOfDamagedFinishTime()
    {
        var calculator = new DownhillPaceCalculator();
        var session = new PaceSessionData(
            PaceSessionType.Final,
            [
                Run("target", [40, 50, 60, 170], finishTimeSeconds: 320),
                Run("rider-2", [41, 51, 61, 71], finishTimeSeconds: 224),
                Run("rider-3", [42, 52, 62, 72], finishTimeSeconds: 228),
                Run("rider-4", [43, 53, 63, 73], finishTimeSeconds: 232),
                Run("rider-5", [44, 54, 64, 74], finishTimeSeconds: 236)
            ]);

        var result = Find(calculator.CalculateSession(session), "target");
        var checkpointScore = calculator.CalculateFullRunTimeScore(
            150,
            [150, 153, 156, 159, 162]);
        var damagedFinishScore = calculator.CalculateFullRunTimeScore(
            320,
            [320, 224, 228, 232, 236]);

        Assert.Equal(3, result.IncidentSectorIndex);
        Assert.Equal(PaceSectorStatus.Incident, result.Sectors[3].Status);
        Assert.Equal(checkpointScore, result.FullRunTimeScore);
        Assert.True(result.FullRunTimeScore > damagedFinishScore);
    }

    [Fact]
    public void DominantRiderAcrossFinalsAndQualifyingScoresBetweenNinetySevenAndOneHundred()
    {
        var calculator = new DownhillPaceCalculator();
        var sessions = new[]
        {
            Find(calculator.CalculateSession(EliteField(PaceSessionType.Final, -0.30)), "target"),
            Find(calculator.CalculateSession(EliteField(PaceSessionType.Final, -0.10)), "target"),
            Find(calculator.CalculateSession(EliteField(PaceSessionType.Qualifying, -0.10)), "target"),
            Find(calculator.CalculateSession(EliteField(
                PaceSessionType.Qualifying,
                targetOffset: 0.7,
                targetFinishRank: 5)), "target")
        };
        var result = calculator.CalculatePace("target", sessions);

        Assert.True(
            result.Pace is >= 97 and <= 100,
            $"Combined Pace: {result.Pace}; session Pace: {string.Join(", ", sessions.Select(session => session.Pace))}");
    }

    [Fact]
    public void DefaultPaceOptionsMatchRequestedValues()
    {
        var options = new PaceOptions();

        Assert.Equal(0.20, options.RankWeight, precision: 10);
        Assert.Equal(0.75, options.TimeWeight, precision: 10);
        Assert.Equal(0.95, options.RankWeight + options.TimeWeight, precision: 10);
        Assert.Equal(5, options.MaximumWinBonus, precision: 10);
        Assert.Equal(1.60, options.SectorRankExponent, precision: 10);
        Assert.Equal(1.25, options.SectorTimeSigmoidSlope, precision: 10);
        Assert.Equal(-0.05, options.SectorTimeSigmoidOffset, precision: 10);
        Assert.Equal(75, options.StrongSectorCalibrationThreshold, precision: 10);
        Assert.Equal(0.60, options.StrongSectorCalibrationPower, precision: 10);
        Assert.Equal(0.50, options.FastSectorFraction, precision: 10);
        Assert.Equal(2, options.MinimumValidSectorsForSessionPace);
        Assert.Equal(4, options.FullEvidenceSectorCount, precision: 10);
        Assert.Equal(4, options.FullConfidenceSectorCount, precision: 10);
        Assert.Equal(0.5, options.CoverageConfidencePower, precision: 10);
        Assert.Equal(30, options.FullConfidenceFieldSize, precision: 10);
        Assert.Equal(4, options.OverallEvidenceScale, precision: 10);
        Assert.Equal(40, options.MinimumConfidenceWithoutFinals, precision: 10);
        Assert.Equal(3, options.FinalEvidenceScale, precision: 10);
        Assert.Equal(0.50, options.FullFinalConfidenceEvidence, precision: 10);
        Assert.Equal(0.75, options.FinalParticipationConfidencePower, precision: 10);
        Assert.Equal(0.70, options.FastSectorWeight, precision: 10);
        Assert.Equal(0.30, options.AllSectorWeight, precision: 10);
        Assert.Equal(2, options.CoverageWeightPower, precision: 10);
        Assert.Equal(0, options.FinishPositiveGain, precision: 10);
        Assert.Equal(0.02, options.TopSpeedPositiveGain, precision: 10);
        Assert.Equal(3.0, options.MaximumDominanceBonus, precision: 10);
        Assert.Equal(0.0025, options.MinimumDominanceRelativeMargin, precision: 10);
        Assert.Equal(0.006, options.DominanceRelativeMarginScale, precision: 10);
        Assert.Equal(0, options.FinalWinBonus, precision: 10);
        Assert.Equal(0, options.SemiFinalWinBonus, precision: 10);
        Assert.Equal(0, options.QualifyingWinBonus, precision: 10);
        Assert.Equal(0, options.SeedingWinBonus, precision: 10);
        Assert.Equal(0, options.TimedTrainingWinBonus, precision: 10);
        Assert.Equal(0, options.TrainingWinBonus, precision: 10);
        Assert.Equal(55, options.PaceCalibrationThreshold, precision: 10);
        Assert.Equal(0.50, options.PaceCalibrationPower, precision: 10);
        Assert.Equal(1.5, options.UpperPaceCalibrationMaximumBoost, precision: 10);
        Assert.Equal(60.0, options.UpperPaceCalibrationStart, precision: 10);
        Assert.Equal(70.0, options.UpperPaceCalibrationFull, precision: 10);
        Assert.Equal(95.0, options.UpperPaceCalibrationTaperStart, precision: 10);
        Assert.Equal(100.0, options.UpperPaceCalibrationEnd, precision: 10);
        Assert.Equal(1.00, options.FinalStageEvidenceFactor, precision: 10);
        Assert.Equal(0.96, options.SemiFinalStageEvidenceFactor, precision: 10);
        Assert.Equal(0.90, options.QualifyingStageEvidenceFactor, precision: 10);
        Assert.Equal(0.80, options.SeedingStageEvidenceFactor, precision: 10);
        Assert.Equal(3.0, options.SemiFinalEarlierStageMaximumLift, precision: 10);
        Assert.Equal(90.0, options.SemiFinalHighestStagePaceCap, precision: 10);
        Assert.Equal(82.0, options.QualifyingHighestStagePaceCap, precision: 10);
        Assert.Equal(75.0, options.SeedingHighestStagePaceCap, precision: 10);
        Assert.Equal(4, options.SparseNonFinalMaximumSectorCount);
        Assert.Equal(78.0, options.SparseSemiFinalPaceCap, precision: 10);
        Assert.Equal(72.0, options.SparseQualifyingPaceCap, precision: 10);
        Assert.Equal(68.0, options.SparseSeedingPaceCap, precision: 10);
        Assert.True(options.CountPositiveSectorsAfterIncident);
        Assert.Equal(50, options.PostIncidentMinimumPositiveScore, precision: 10);
        Assert.Equal(-1, options.PostIncidentMinimumPerformanceZ, precision: 10);
        Assert.Equal(15, options.PostIncidentMaximumScoreDrop, precision: 10);
        Assert.Equal(0.20, options.PostIncidentMaximumPercentileDrop, precision: 10);
        Assert.Equal(1.5, options.PostIncidentMaximumPerformanceDrop, precision: 10);
    }

    [Fact]
    public void SectorWeightsAboveNinetyFivePercentAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DownhillPaceCalculator(new PaceOptions
            {
                RankWeight = 0.50,
                TimeWeight = 0.50
            }));
    }

    [Fact]
    public void SectorWinBonusAboveFivePointsIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DownhillPaceCalculator(new PaceOptions
            {
                MaximumWinBonus = 5.01
            }));
    }

    [Fact]
    public void MedianSectorTimeUsesConfiguredSigmoidOffset()
    {
        Assert.Equal(
            100.0 / (1.0 + Math.Exp(-0.05)),
            new DownhillPaceCalculator().CalculateSectorTimeScore(performanceZ: 0),
            precision: 10);
    }

    [Fact]
    public void TimeImprovementHasMoreImpactThanEqualRankImprovement()
    {
        var calculator = new DownhillPaceCalculator();
        var baseline = calculator.CalculateRawSectorScore(rankScore: 50, timeScore: 50);
        var rankImprovement = calculator.CalculateRawSectorScore(rankScore: 60, timeScore: 50) - baseline;
        var timeImprovement = calculator.CalculateRawSectorScore(rankScore: 50, timeScore: 60) - baseline;

        Assert.Equal(2.0, rankImprovement, precision: 10);
        Assert.Equal(7.5, timeImprovement, precision: 10);
        Assert.True(timeImprovement > rankImprovement);
    }

    [Fact]
    public void FourSectorsUseConfiguredSeventyThirtyBlend()
    {
        var calculator = new DownhillPaceCalculator();
        var scores = new[]
        {
            new WeightedScore(100, 1),
            new WeightedScore(80, 1),
            new WeightedScore(60, 1),
            new WeightedScore(40, 1)
        };

        var sectorPace = calculator.CalculateSectorPace(scores);

        Assert.Equal(84, sectorPace, precision: 10);
    }

    [Fact]
    public void FinalPaceCalibrationLeavesValuesThroughFiftyFiveUnchanged()
    {
        var calculator = new DownhillPaceCalculator();

        Assert.Equal(50, calculator.ApplyFinalPaceCalibration(50), precision: 10);
        Assert.Equal(55, calculator.ApplyFinalPaceCalibration(55), precision: 10);
    }

    [Fact]
    public void RawPaceEightyEightCalibratesToAboutNinetyThreePointFive()
    {
        var calibrated = new DownhillPaceCalculator().ApplyFinalPaceCalibration(88);

        Assert.InRange(calibrated, 93, 94);
    }

    [Fact]
    public void FinalPaceCalibrationIsStrictlyMonotonic()
    {
        var calculator = new DownhillPaceCalculator();
        var values = Enumerable.Range(0, 101)
            .Select(value => calculator.ApplyFinalPaceCalibration(value))
            .ToArray();

        for (var index = 1; index < values.Length; index++)
        {
            Assert.True(values[index] > values[index - 1]);
        }
    }

    [Theory]
    [InlineData(60.0, 60.0)]
    [InlineData(65.0, 65.75)]
    [InlineData(70.0, 71.5)]
    [InlineData(80.0, 81.5)]
    [InlineData(90.0, 91.5)]
    [InlineData(95.0, 96.5)]
    [InlineData(97.5, 98.25)]
    [InlineData(100.0, 100.0)]
    public void UpperPaceCalibrationAppliesSmoothOnePointFiveBoost(
        double pace,
        double expected)
    {
        var calibrated = new DownhillPaceCalculator()
            .ApplyUpperPaceCalibration(pace);

        Assert.Equal(expected, calibrated, precision: 10);
    }

    [Fact]
    public void UpperPaceCalibrationIsStrictlyMonotonicBelowOneHundred()
    {
        var calculator = new DownhillPaceCalculator();
        var values = Enumerable.Range(0, 1000)
            .Select(index => calculator.ApplyUpperPaceCalibration(index / 10.0))
            .ToArray();

        for (var index = 1; index < values.Length; index++)
        {
            Assert.True(values[index] > values[index - 1]);
        }
    }

    [Theory]
    [InlineData(nameof(PaceSessionType.Final), 4.00)]
    [InlineData(nameof(PaceSessionType.SemiFinal), 2.0)]
    [InlineData(nameof(PaceSessionType.Qualifying1), 1.5)]
    [InlineData(nameof(PaceSessionType.Qualifying2), 1.5)]
    [InlineData(nameof(PaceSessionType.Qualifying), 1.5)]
    [InlineData(nameof(PaceSessionType.Seeding), 0.6)]
    [InlineData(nameof(PaceSessionType.TimedTraining), 0.00)]
    [InlineData(nameof(PaceSessionType.Training), 0.00)]
    public void SessionTypeHasExactConfiguredWeight(
        string sessionTypeName,
        double expectedWeight)
    {
        var sessionType = Enum.Parse<PaceSessionType>(sessionTypeName);

        Assert.Equal(expectedWeight, SessionWeightFor(sessionType), precision: 10);
    }

    [Theory]
    [InlineData("Q1")]
    [InlineData("Q2")]
    public void QualificationTypesHaveSeparateConfigEntries(string sourceType)
    {
        var parser = typeof(PaceService).GetMethod(
            "ParseSessionType",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static);
        var sessionType = Assert.IsType<PaceSessionType>(
            parser!.Invoke(null, [sourceType, null]));

        Assert.Equal(
            sourceType == "Q1" ? PaceSessionType.Qualifying1 : PaceSessionType.Qualifying2,
            sessionType);
        Assert.Equal(
            SessionWeightFor(PaceSessionType.Qualifying),
            SessionWeightFor(sessionType),
            precision: 10);
    }

    [Fact]
    public void FinalSessionWeightIsFour()
    {
        var finalWeight = SessionWeightFor(PaceSessionType.Final);

        Assert.Equal(4.0, finalWeight, precision: 10);
    }

    [Fact]
    public void AllOfficialSessionWeightsComeFromCentralConfiguration()
    {
        var qualifyingWeight = SessionWeightFor(PaceSessionType.Qualifying);
        var semiFinalWeight = SessionWeightFor(PaceSessionType.SemiFinal);

        var seedingWeight = SessionWeightFor(PaceSessionType.Seeding);
        var finalWeight = SessionWeightFor(PaceSessionType.Final);

        Assert.Equal(4.0, finalWeight, precision: 10);
        Assert.Equal(2.0, semiFinalWeight, precision: 10);
        Assert.Equal(1.5, qualifyingWeight, precision: 10);
        Assert.Equal(0.6, seedingWeight, precision: 10);
    }

    [Fact]
    public void QualifyingSessionsAreAggregatedIntoOneStageBeforeFinalWeighting()
    {
        var calculator = new DownhillPaceCalculator();
        var q1 = SessionResult(92, 1, SessionWeightFor(PaceSessionType.Qualifying1), sessionType: PaceSessionType.Qualifying1);
        var q2 = SessionResult(82, 1, SessionWeightFor(PaceSessionType.Qualifying2), sessionType: PaceSessionType.Qualifying2);
        var final = SessionResult(88, 1, SessionWeightFor(PaceSessionType.Final), sessionType: PaceSessionType.Final);
        var qualifyingStage = calculator.CalculateStagePace([q1, q2])!;

        var result = calculator.CalculatePace("target", [q1, q2, final]);
        var finalStage = calculator.CalculateStagePace([final])!;
        var expected = calculator.CalculatePaceWithTargetFinalImpact(
            finalStage,
            [qualifyingStage]);

        Assert.Equal(2, qualifyingStage.UsedSessions);
        Assert.Equal(expected, result.RawPace!.Value, precision: 10);
        Assert.Equal(3, result.UsedSessions);
    }

    [Fact]
    public void DuplicateIdenticalQualifyingDoesNotGainAnotherStageImpactAgainstFinal()
    {
        var calculator = new DownhillPaceCalculator();
        var qualifying = SessionResult(
            96,
            1,
            SessionWeightFor(PaceSessionType.Qualifying),
            sessionType: PaceSessionType.Qualifying);
        var final = SessionResult(80, 1, SessionWeightFor(PaceSessionType.Final), sessionType: PaceSessionType.Final);

        var oneQualifying = calculator.CalculatePace("target", [qualifying, final]);
        var twoQualifying = calculator.CalculatePace("target", [qualifying, qualifying, final]);

        Assert.Equal(oneQualifying.RawPace, twoQualifying.RawPace);
    }

    [Fact]
    public void FinalStageUsesConfiguredNormalizedStructure()
    {
        var weights = new SessionWeightOptions();
        var allStages = DownhillPaceCalculator.CalculateCombinedStagePace(
            Stage(100, weights.Final),
            Stage(50, weights.SemiFinal),
            Stage(25, weights.Qualifying),
            Stage(0, weights.Seeding));
        var onlyFinal = DownhillPaceCalculator.CalculateCombinedStagePace(Stage(90, weights.Final));
        var finalAndQualifying = DownhillPaceCalculator.CalculateCombinedStagePace(
            Stage(90, weights.Final),
            Stage(60, weights.Qualifying));

        Assert.Equal(
            (weights.Final * 100 + weights.SemiFinal * 50 + weights.Qualifying * 25) /
            (weights.Final + weights.SemiFinal + weights.Qualifying + weights.Seeding),
            allStages,
            precision: 10);
        Assert.Equal(90, onlyFinal, precision: 10);
        Assert.Equal(
            (weights.Final * 90 + weights.Qualifying * 60) /
            (weights.Final + weights.Qualifying),
            finalAndQualifying,
            precision: 10);
    }

    [Fact]
    public void MissingStagesAreNotAddedAsZeroScores()
    {
        var weights = new SessionWeightOptions();
        var semiFinalAndQualifying = DownhillPaceCalculator.CalculateCombinedStagePace(
            Stage(100, weights.SemiFinal),
            Stage(50, weights.Qualifying));
        var semiFinalAndSeeding = DownhillPaceCalculator.CalculateCombinedStagePace(
            Stage(90, weights.SemiFinal),
            Stage(50, weights.Seeding));

        Assert.Equal(
            (weights.SemiFinal * 100 + weights.Qualifying * 50) /
            (weights.SemiFinal + weights.Qualifying),
            semiFinalAndQualifying,
            precision: 10);
        Assert.Equal(
            (weights.SemiFinal * 90 + weights.Seeding * 50) /
            (weights.SemiFinal + weights.Seeding),
            semiFinalAndSeeding,
            precision: 10);
    }

    [Fact]
    public void SupportedRoundFormatsUseOnlySessionsThatExist()
    {
        var calculator = new DownhillPaceCalculator();
        var formats = new PaceSessionType[][]
        {
            [PaceSessionType.Qualifying1, PaceSessionType.Qualifying2, PaceSessionType.Final],
            [PaceSessionType.Seeding, PaceSessionType.Final],
            [PaceSessionType.Qualifying, PaceSessionType.SemiFinal, PaceSessionType.Final],
            [PaceSessionType.Qualifying, PaceSessionType.Final],
            [PaceSessionType.Final]
        };

        foreach (var format in formats)
        {
            var sessions = format
                .Select(type => SessionResult(
                    85,
                    1,
                    SessionWeightFor(type),
                    sessionType: type))
                .ToArray();

            var result = calculator.CalculatePace("target", sessions);

            Assert.NotNull(result.Pace);
            Assert.Equal(format.Length, result.UsedSessions);
        }
    }

    [Fact]
    public void DisqualifiedRunIsRemovedFromSessionPopulation()
    {
        var calculator = new DownhillPaceCalculator();
        var validRuns = new[]
        {
            Run("target", [40, 50], finishRank: 1),
            Run("rider-2", [42, 52], finishRank: 2),
            Run("rider-3", [44, 54], finishRank: 3)
        };
        var withDisqualified = validRuns.Append(
            Run("dsq", [1, 1], finishRank: 1, status: "DSQ")).ToArray();

        var baseline = Find(calculator.CalculateSession(new PaceSessionData(
            PaceSessionType.Final,
            validRuns)), "target");
        var filtered = calculator.CalculateSession(new PaceSessionData(
            PaceSessionType.Final,
            withDisqualified));

        Assert.DoesNotContain(filtered, result => result.RiderId == "dsq");
        Assert.Equal(baseline.Pace, Find(filtered, "target").Pace);
        Assert.Equal(baseline.Sectors, Find(filtered, "target").Sectors);
    }

    [Fact]
    public void NonFinalistCapIsConfigurableAndDoesNotAffectQualifiedRider()
    {
        var calculator = new DownhillPaceCalculator(new PaceOptions
        {
            NonFinalistMaxScore = 80
        });

        Assert.Equal(80, calculator.ApplyNonFinalistScoreCap(95, isNonFinalist: true));
        Assert.Equal(95, calculator.ApplyNonFinalistScoreCap(95, isNonFinalist: false));
    }

    [Fact]
    public void FinalHasEightyFivePercentImpactInSupportedRoundFormats()
    {
        var calculator = new DownhillPaceCalculator();
        var weights = new SessionWeightOptions();
        var final = Stage(100, weights.Final);

        var q1Q2Final = calculator.CalculatePaceWithTargetFinalImpact(
            final,
            [Stage(0, weights.Qualifying1 + weights.Qualifying2)]);
        var qualifyingFinal = calculator.CalculatePaceWithTargetFinalImpact(
            final,
            [Stage(0, weights.Qualifying)]);
        var qualifyingSemiFinal = calculator.CalculatePaceWithTargetFinalImpact(
            final,
            [Stage(0, weights.Qualifying), Stage(0, weights.SemiFinal)]);

        Assert.Equal(85, q1Q2Final, precision: 10);
        Assert.Equal(85, qualifyingFinal, precision: 10);
        Assert.Equal(85, qualifyingSemiFinal, precision: 10);

        var q1Q2Weights = weights.ApplyFinalTargetImpact(
            [PaceSessionType.Qualifying1, PaceSessionType.Qualifying2, PaceSessionType.Final],
            [weights.Qualifying1, weights.Qualifying2, weights.Final]);
        var qualifyingFinalWeights = weights.ApplyFinalTargetImpact(
            [PaceSessionType.Qualifying, PaceSessionType.Final],
            [weights.Qualifying, weights.Final]);
        var qualifyingSemiFinalWeights = weights.ApplyFinalTargetImpact(
            [PaceSessionType.Qualifying, PaceSessionType.SemiFinal, PaceSessionType.Final],
            [weights.Qualifying, weights.SemiFinal, weights.Final]);

        Assert.Equal(0.075, q1Q2Weights[0], precision: 10);
        Assert.Equal(0.075, q1Q2Weights[1], precision: 10);
        Assert.Equal(0.85, q1Q2Weights[2], precision: 10);
        Assert.Equal(0.15, qualifyingFinalWeights[0], precision: 10);
        Assert.Equal(0.85, qualifyingFinalWeights[1], precision: 10);
        Assert.Equal(0.15 * 1.5 / 3.5, qualifyingSemiFinalWeights[0], precision: 10);
        Assert.Equal(0.15 * 2.0 / 3.5, qualifyingSemiFinalWeights[1], precision: 10);
        Assert.Equal(0.85, qualifyingSemiFinalWeights[2], precision: 10);
    }

    [Theory]
    [InlineData(nameof(PaceSessionType.Final), 91, 91)]
    [InlineData(nameof(PaceSessionType.SemiFinal), 91, 89.36)]
    [InlineData(nameof(PaceSessionType.Qualifying), 91, 86.9)]
    [InlineData(nameof(PaceSessionType.Seeding), 91, 82.8)]
    public void HighestStageEvidenceLimitsOnlyScoreAboveFifty(
        string stageName,
        double pace,
        double expected)
    {
        var calculator = new DownhillPaceCalculator();
        var stage = Enum.Parse<PaceSessionType>(stageName);

        Assert.Equal(expected, calculator.ApplyHighestStageEvidence(pace, stage), precision: 10);
        Assert.Equal(45, calculator.ApplyHighestStageEvidence(45, stage), precision: 10);
    }

    [Theory]
    [InlineData(nameof(PaceSessionType.Final), 83.0)]
    [InlineData(nameof(PaceSessionType.SemiFinal), 82.0)]
    [InlineData(nameof(PaceSessionType.Qualifying), 81.0)]
    [InlineData(nameof(PaceSessionType.Seeding), 80.0)]
    public void FullRunTimeScoreUsesStageSpecificShare(string stageName, double expected)
    {
        var stage = Enum.Parse<PaceSessionType>(stageName);
        var combined = DownhillPaceCalculator.CombineSectorAndFullRunPace(stage, 80, 100);

        Assert.Equal(expected, combined, precision: 10);
    }

    [Fact]
    public void DominantFinalOutranksStrongQualifyingOnlyPerformance()
    {
        var calculator = new DownhillPaceCalculator();
        var qualifyingOnly = calculator.CalculatePace(
            "target",
            [SessionResult(91, 1, SessionWeightFor(PaceSessionType.Qualifying), sessionType: PaceSessionType.Qualifying)]);
        var dominantFinal = calculator.CalculatePace(
            "target",
            [
                SessionResult(80, 1, SessionWeightFor(PaceSessionType.Qualifying), sessionType: PaceSessionType.Qualifying),
                SessionResult(94, 1, SessionWeightFor(PaceSessionType.Final), sessionType: PaceSessionType.Final)
            ]);

        Assert.Equal(86.9, qualifyingOnly.RawPace!.Value, precision: 10);
        Assert.Equal(82.0, qualifyingOnly.Pace!.Value, precision: 10);
        Assert.True(dominantFinal.RawPace > qualifyingOnly.RawPace);
        Assert.True(dominantFinal.Pace > qualifyingOnly.Pace);
    }

    [Fact]
    public void QualifyingOnlyCannotReachEliteFinalPaceRange()
    {
        var result = new DownhillPaceCalculator().CalculatePace(
            "target",
            [SessionResult(100, 1, SessionWeightFor(PaceSessionType.Qualifying), sessionType: PaceSessionType.Qualifying)]);

        Assert.Equal(95, result.RawPace!.Value, precision: 10);
        Assert.Equal(82, result.Pace!.Value, precision: 10);
    }

    [Theory]
    [InlineData(nameof(PaceSessionType.SemiFinal), 78.0)]
    [InlineData(nameof(PaceSessionType.Qualifying), 72.0)]
    [InlineData(nameof(PaceSessionType.Seeding), 68.0)]
    public void FourSectorsWithoutFinalReceiveStrongSparseEvidenceCap(
        string stageName,
        double expectedCap)
    {
        var stage = Enum.Parse<PaceSessionType>(stageName);
        var result = new DownhillPaceCalculator().CalculatePace(
            "target",
            [SessionResult(100, 1, 1, sessionType: stage, usedSectorCount: 4)]);

        Assert.Equal(expectedCap, result.Pace!.Value, precision: 10);
    }

    [Fact]
    public void FiveQualifyingSectorsUseStageCapInsteadOfSparseEvidenceCap()
    {
        var result = new DownhillPaceCalculator().CalculatePace(
            "target",
            [SessionResult(
                100,
                1,
                SessionWeightFor(PaceSessionType.Qualifying),
                sessionType: PaceSessionType.Qualifying,
                usedSectorCount: 5)]);

        Assert.Equal(82, result.Pace!.Value, precision: 10);
    }

    [Fact]
    public void FinalWithThreeSectorsIsExemptFromSparseEvidenceCap()
    {
        var calculator = new DownhillPaceCalculator();
        var result = calculator.CalculatePace(
            "target",
            [SessionResult(
                95,
                1,
                SessionWeightFor(PaceSessionType.Final),
                sessionType: PaceSessionType.Final,
                usedSectorCount: 3)]);

        Assert.Equal(
            calculator.ApplyUpperPaceCalibration(
                calculator.ApplyFinalPaceCalibration(95)),
            result.Pace!.Value,
            precision: 10);
        Assert.True(result.Pace > 90);
    }

    [Fact]
    public void FastQualifyingCannotHideSlowSemiFinal()
    {
        var calculator = new DownhillPaceCalculator();
        var result = calculator.CalculatePace(
            "target",
            [
                SessionResult(100, 1, SessionWeightFor(PaceSessionType.Qualifying), sessionType: PaceSessionType.Qualifying),
                SessionResult(70, 1, SessionWeightFor(PaceSessionType.SemiFinal), sessionType: PaceSessionType.SemiFinal)
            ]);
        var maximumRawBeforeEvidence = 70 + 3;
        var expectedEvidenceAdjustedMaximum =
            50 + (maximumRawBeforeEvidence - 50) * 0.96;

        Assert.Equal(expectedEvidenceAdjustedMaximum, result.RawPace!.Value, precision: 10);
        Assert.True(result.Pace < 86);
    }

    [Fact]
    public void NewCalibrationIsGloballyMorePositiveAboveThreshold()
    {
        var current = new DownhillPaceCalculator();
        var previous = new DownhillPaceCalculator(new PaceOptions
        {
            PaceCalibrationPower = 0.65
        });

        Assert.True(current.ApplyFinalPaceCalibration(80) > previous.ApplyFinalPaceCalibration(80));
        Assert.Equal(50, current.ApplyFinalPaceCalibration(50), precision: 10);
    }

    [Fact]
    public void ZeroWeightSessionsDoNotChangeCombinedPaceOrUsedSessions()
    {
        var calculator = new DownhillPaceCalculator();
        var final = SessionResult(
            pace: 80,
            confidence: 0.8,
            sessionWeight: SessionWeightFor(PaceSessionType.Final),
            sessionType: PaceSessionType.Final);
        var timedTraining = SessionResult(
            pace: 100,
            confidence: 1.0,
            sessionWeight: 0.0,
            sessionType: PaceSessionType.TimedTraining);
        var training = SessionResult(
            pace: 100,
            confidence: 1.0,
            sessionWeight: 0.0,
            sessionType: PaceSessionType.Training);

        var finalOnly = calculator.CalculatePace("target", [final]);
        var withZeroWeightSessions = calculator.CalculatePace(
            "target",
            [final, timedTraining, training]);

        Assert.Equal(finalOnly.Pace, withZeroWeightSessions.Pace);
        Assert.Equal(finalOnly.RawPace, withZeroWeightSessions.RawPace);
        Assert.Equal(finalOnly.Confidence, withZeroWeightSessions.Confidence);
        Assert.Equal(1, withZeroWeightSessions.UsedSessions);
    }

    [Fact]
    public void StrongFinalHasMoreInfluenceThanEquallyStrongQualifying()
    {
        var calculator = new DownhillPaceCalculator();
        var finalWeight = SessionWeightFor(PaceSessionType.Final);
        var qualifyingWeight = SessionWeightFor(PaceSessionType.Qualifying);

        var strongFinal = calculator.CalculatePace(
            "target",
            [
                SessionResult(96, 1, finalWeight, sessionType: PaceSessionType.Final),
                SessionResult(88, 1, qualifyingWeight, sessionType: PaceSessionType.Qualifying)
            ]);
        var strongQualifying = calculator.CalculatePace(
            "target",
            [
                SessionResult(88, 1, finalWeight, sessionType: PaceSessionType.Final),
                SessionResult(96, 1, qualifyingWeight, sessionType: PaceSessionType.Qualifying)
            ]);

        Assert.True(strongFinal.RawPace > strongQualifying.RawPace);
        Assert.Equal(2, strongFinal.UsedSessions);
        Assert.Equal(2, strongQualifying.UsedSessions);
    }

    [Fact]
    public void IdenticalSectorsHaveSameSessionPaceButDifferentSessionWeight()
    {
        var calculator = new DownhillPaceCalculator();
        var source = SingleSectorField();
        var final = Find(
            calculator.CalculateSession(source with { SessionType = PaceSessionType.Final }),
            "rider-0");
        var qualifying = Find(
            calculator.CalculateSession(source with { SessionType = PaceSessionType.Qualifying }),
            "rider-0");

        Assert.Equal(qualifying.Pace, final.Pace);
        Assert.Equal(4.0, final.SessionWeight, precision: 10);
        Assert.Equal(new SessionWeightOptions().Qualifying, qualifying.SessionWeight, precision: 10);
    }

    [Fact]
    public void TrainingOnlyReturnsNoPace()
    {
        var training = SessionResult(
            pace: 100,
            confidence: 1.0,
            sessionWeight: 0.0,
            sessionType: PaceSessionType.Training);

        var result = new DownhillPaceCalculator().CalculatePace("target", [training]);

        Assert.Null(result.Pace);
        Assert.Null(result.RawPace);
        Assert.Equal(0, result.Confidence);
        Assert.Equal(0, result.UsedSessions);
    }

    [Fact]
    public void CombinedPaceReturnsRawAndCalibratedValues()
    {
        var calculator = new DownhillPaceCalculator();

        var result = calculator.CalculatePace(
            "target",
            [SessionResult(88, 1, 1, sessionType: PaceSessionType.Final)]);

        Assert.Equal(88, result.RawPace!.Value, precision: 10);
        Assert.Equal(
            calculator.ApplyUpperPaceCalibration(
                calculator.ApplyFinalPaceCalibration(88)),
            result.Pace!.Value,
            precision: 10);
        Assert.True(result.Pace > result.RawPace);
    }

    [Fact]
    public void TopSpeedBoostCanRaisePaceButCannotLowerIt()
    {
        var positiveBoost = DownhillPaceCalculator.CalculatePositiveBoost(
            score: 98,
            sectorPace: 88,
            gain: 0.02);
        var negativeDifference = DownhillPaceCalculator.CalculatePositiveBoost(
            score: 78,
            sectorPace: 88,
            gain: 0.02);

        Assert.Equal(0.2, positiveBoost, precision: 10);
        Assert.Equal(0, negativeDifference);
    }

    [Fact]
    public void RiderAroundThirtiethPlaceDoesNotReceiveHighRankScore()
    {
        var percentile = (100.0 - 30.0) / 99.0;

        var rankScore = new DownhillPaceCalculator()
            .CalculateSectorRankScore(percentile);

        Assert.True(rankScore < 60);
    }

    [Fact]
    public void OneSectorIsInsufficientForSessionPace()
    {
        var result = Find(
            new DownhillPaceCalculator().CalculateSession(SingleSectorField()),
            "rider-0");

        Assert.Null(result.Pace);
        Assert.False(result.IsRepresentative);
        Assert.Equal("InsufficientSectorEvidence", result.NoResultReason);
    }

    [Fact]
    public void TwoOfFiveSectorsAreAdjustedForLimitedEvidence()
    {
        var result = Find(
            new DownhillPaceCalculator().CalculateSession(PartialFiveSectorField()),
            "target");
        var expectedFactor = Math.Sqrt(0.4) * 0.5;
        var expectedAdjustedPace = 50.0 +
            (result.SectorPace!.Value - 50.0) * expectedFactor;

        Assert.Equal(0.4, result.SectorCoverage, precision: 10);
        Assert.True(result.SectorPace > 50);
        Assert.Equal(
            expectedAdjustedPace,
            result.EvidenceAdjustedSectorPace!.Value,
            precision: 10);
        Assert.True(result.EvidenceAdjustedSectorPace < result.SectorPace);
    }

    [Fact]
    public void FewerThanFourSectorsUseAllSectorMeanOnly()
    {
        var session = ThreeSectorField();
        var fastOnly = Find(new DownhillPaceCalculator(new PaceOptions
        {
            FastSectorWeight = 1,
            AllSectorWeight = 0
        }).CalculateSession(session), "target");
        var allOnly = Find(new DownhillPaceCalculator(new PaceOptions
        {
            FastSectorWeight = 0,
            AllSectorWeight = 1
        }).CalculateSession(session), "target");

        Assert.Equal(fastOnly.SectorPace, allOnly.SectorPace);
    }

    [Fact]
    public void FortyPercentCoverageIsSquaredInEffectivePaceWeight()
    {
        var calculator = new DownhillPaceCalculator();
        var full = calculator.CalculateStagePace(
            [SessionResult(80, 1, 1, sectorCoverage: 1, sessionType: PaceSessionType.Final)]);
        var partial = calculator.CalculateStagePace(
            [SessionResult(80, 1, 1, sectorCoverage: 0.4, sessionType: PaceSessionType.Final)]);

        Assert.Equal(1, full!.EvidenceWeight, precision: 10);
        Assert.Equal(0.16, partial!.EvidenceWeight, precision: 10);
    }

    [Fact]
    public void HigherCompetitionClassHasTwiceTheImpactOnRatingPace()
    {
        var calculator = new DownhillPaceCalculator();
        var sessions = new[]
        {
            SessionResult(
                95,
                1,
                SessionWeightFor(PaceSessionType.Final),
                sessionType: PaceSessionType.Final,
                competitionClassPriority: 1),
            SessionResult(
                75,
                1,
                SessionWeightFor(PaceSessionType.Final),
                sessionType: PaceSessionType.Final,
                competitionClassPriority: 2)
        };

        var unweighted = calculator.CalculateStagePace(sessions);
        var weighted = calculator.CalculateStagePace(
            sessions,
            CompetitionClassWeighting.FromPriorities([1, 2]));

        Assert.True(weighted!.Pace > unweighted!.Pace);
        Assert.Equal(3, weighted.EvidenceWeight, precision: 10);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void SingleCompetitionClassDoesNotChangeRatingPace(int priority)
    {
        var calculator = new DownhillPaceCalculator();
        var sessions = new[]
        {
            SessionResult(
                95,
                1,
                SessionWeightFor(PaceSessionType.Final),
                sessionType: PaceSessionType.Final,
                competitionClassPriority: priority),
            SessionResult(
                75,
                1,
                SessionWeightFor(PaceSessionType.Final),
                sessionType: PaceSessionType.Final,
                competitionClassPriority: priority)
        };

        var unweighted = calculator.CalculateStagePace(sessions);
        var weighted = calculator.CalculateStagePace(
            sessions,
            CompetitionClassWeighting.FromPriorities([priority]));

        Assert.Equal(unweighted, weighted);
    }

    [Fact]
    public void SessionConfidenceUsesZeroToOneHundredScaleAndConfiguredFormula()
    {
        var result = Find(
            new DownhillPaceCalculator().CalculateSession(ConfidenceField(
                PaceSessionType.Final,
                sectorCount: 4,
                fieldSize: 30)),
            "target");
        var expected = 100.0 * (1.0 - Math.Exp(-1.0));

        Assert.Equal(expected, result.Confidence, precision: 10);
        Assert.InRange(result.Confidence, 0, 100);
    }

    [Fact]
    public void MoreValidSectorsAndCoverageIncreaseSessionConfidence()
    {
        var calculator = new DownhillPaceCalculator();
        var partial = Find(calculator.CalculateSession(ConfidenceField(
            PaceSessionType.Final,
            sectorCount: 4,
            fieldSize: 30,
            targetValidSectorCount: 2)), "target");
        var complete = Find(calculator.CalculateSession(ConfidenceField(
            PaceSessionType.Final,
            sectorCount: 4,
            fieldSize: 30)), "target");

        Assert.True(complete.Confidence > partial.Confidence);
    }

    [Fact]
    public void LargerFieldIncreasesSessionConfidence()
    {
        var calculator = new DownhillPaceCalculator();
        var smallField = Find(calculator.CalculateSession(ConfidenceField(
            PaceSessionType.Final,
            sectorCount: 4,
            fieldSize: 10)), "target");
        var fullField = Find(calculator.CalculateSession(ConfidenceField(
            PaceSessionType.Final,
            sectorCount: 4,
            fieldSize: 30)), "target");

        Assert.True(fullField.Confidence > smallField.Confidence);
    }

    [Fact]
    public void NonRepresentativeRunHasZeroConfidence()
    {
        var runs = ConfidenceField(PaceSessionType.Final, 4, 30).Runs.ToArray();
        runs[0] = new PaceRiderRun(
            "target",
            [100, 110, 120, 130],
            HistoricalExpectedPercentile: 1.0);
        var result = Find(
            new DownhillPaceCalculator().CalculateSession(new PaceSessionData(
                PaceSessionType.Final,
                runs)),
            "target");

        Assert.False(result.IsRepresentative);
        Assert.Equal(0, result.Confidence);
    }

    [Fact]
    public void IncidentAndRecoveredSectorsDoNotCountAsConfidenceEvidence()
    {
        var calculator = new DownhillPaceCalculator();
        var complete = Find(calculator.CalculateSession(new PaceSessionData(
            PaceSessionType.Final,
            [
                Run("target", [40, 50, 60, 70]),
                Run("rider-2", [41, 51, 61, 71]),
                Run("rider-3", [42, 52, 62, 72]),
                Run("rider-4", [43, 53, 63, 73]),
                Run("rider-5", [44, 54, 64, 74])
            ])), "target");
        var incident = Find(calculator.CalculateSession(new PaceSessionData(
            PaceSessionType.Final,
            [
                Run("target", [40, 50, 120, 70]),
                Run("rider-2", [41, 51, 61, 71]),
                Run("rider-3", [42, 52, 62, 72]),
                Run("rider-4", [43, 53, 63, 73]),
                Run("rider-5", [44, 54, 64, 74])
            ])), "target");

        Assert.Equal(PaceSectorStatus.RecoveredAfterIncident, incident.Sectors[3].Status);
        Assert.True(incident.Confidence < complete.Confidence);
    }

    [Fact]
    public void SessionTypeWeightsOrderOverallConfidenceEvidence()
    {
        var calculator = new DownhillPaceCalculator();
        var qualifying = calculator.CalculatePace("target", [SessionResult(
            80, 1, SessionWeightFor(PaceSessionType.Qualifying),
            sessionType: PaceSessionType.Qualifying)]);
        var semiFinal = calculator.CalculatePace("target", [SessionResult(
            80, 1, SessionWeightFor(PaceSessionType.SemiFinal),
            sessionType: PaceSessionType.SemiFinal)]);
        var final = calculator.CalculatePace("target", [SessionResult(
            80, 1, SessionWeightFor(PaceSessionType.Final),
            sessionType: PaceSessionType.Final)]);

        Assert.True(final.Confidence > semiFinal.Confidence);
        Assert.True(semiFinal.Confidence > qualifying.Confidence);
    }

    [Fact]
    public void ManyQualifyingSessionsCannotExceedNoFinalCap()
    {
        var qualifying = Enumerable.Range(0, 100)
            .Select(_ => SessionResult(
                80,
                1,
                SessionWeightFor(PaceSessionType.Qualifying),
                sessionType: PaceSessionType.Qualifying))
            .ToArray();

        var result = new DownhillPaceCalculator().CalculatePace("target", qualifying);

        Assert.Equal(40, result.Confidence, precision: 10);
        Assert.Equal(0, result.UsedFinals);
        Assert.Equal(0, result.FinalEvidence);
    }

    [Fact]
    public void CompleteFinalsProgressivelyRaiseConfidenceCap()
    {
        var calculator = new DownhillPaceCalculator();
        var oneFinal = OverallConfidenceForFinalCount(calculator, 1);
        var threeFinals = OverallConfidenceForFinalCount(calculator, 3);
        var sixFinals = OverallConfidenceForFinalCount(calculator, 6);

        Assert.InRange(oneFinal.Confidence, 45, 75);
        Assert.True(threeFinals.Confidence > oneFinal.Confidence);
        Assert.True(sixFinals.Confidence > threeFinals.Confidence);
        Assert.Equal(6, sixFinals.UsedFinals);
        Assert.Equal(6, sixFinals.FinalEvidence, precision: 10);
    }

    [Fact]
    public void OneCompleteFinalOutOfSixHasLowConfidence()
    {
        var calculator = new DownhillPaceCalculator();
        var final = SessionResult(
            90,
            confidence: 0.60,
            SessionWeightFor(PaceSessionType.Final),
            sessionType: PaceSessionType.Final);

        var result = calculator.CalculatePace(
            "target",
            [final],
            availableFinalSessions: 6);
        var expectedCap = 100.0 * Math.Pow(0.60 / (6 * 0.50), 0.75);

        Assert.Equal(expectedCap, result.Confidence, precision: 10);
        Assert.Equal("Low", result.ConfidenceStatus);
        Assert.InRange(result.Confidence, 25, 35);
    }

    [Fact]
    public void NoUsableFinalEvidenceIsPenalizedByAvailableFinalCount()
    {
        var calculator = new DownhillPaceCalculator();

        var cap = calculator.CalculateFinalConfidenceCap(0, 6);

        Assert.InRange(cap, 10, 11);
    }

    [Fact]
    public void SixCompleteFinalsProduceHighConfidenceWithCurrentFinalWeight()
    {
        var calculator = new DownhillPaceCalculator();
        var finals = Enumerable.Range(0, 6)
            .Select(_ => SessionResult(
                80,
                confidence: 0.60,
                SessionWeightFor(PaceSessionType.Final),
                sessionType: PaceSessionType.Final))
            .ToArray();

        var result = calculator.CalculatePace(
            "target",
            finals,
            availableFinalSessions: 6);

        Assert.InRange(result.Confidence, 97, 98);
        Assert.Equal("High", result.ConfidenceStatus);
    }

    [Fact]
    public void AvailableFinalCountChangesConfidenceButNotPace()
    {
        var calculator = new DownhillPaceCalculator();
        var sessions = new[]
        {
            SessionResult(
                90,
                confidence: 0.60,
                SessionWeightFor(PaceSessionType.Final),
                sessionType: PaceSessionType.Final),
            SessionResult(
                85,
                confidence: 0.60,
                SessionWeightFor(PaceSessionType.Qualifying),
                sessionType: PaceSessionType.Qualifying)
        };

        var oneAvailable = calculator.CalculatePace(
            "target",
            sessions,
            availableFinalSessions: 1);
        var sixAvailable = calculator.CalculatePace(
            "target",
            sessions,
            availableFinalSessions: 6);

        Assert.Equal(oneAvailable.Pace, sixAvailable.Pace);
        Assert.Equal(oneAvailable.RawPace, sixAvailable.RawPace);
        Assert.True(oneAvailable.Confidence > sixAvailable.Confidence);
    }

    [Fact]
    public void SessionConfidenceChangesReportedConfidenceButNotPace()
    {
        var calculator = new DownhillPaceCalculator();
        var highConfidence = calculator.CalculatePace(
            "target",
            [
                SessionResult(94, 1.0, SessionWeightFor(PaceSessionType.Final), sessionType: PaceSessionType.Final),
                SessionResult(72, 1.0, SessionWeightFor(PaceSessionType.Final), sessionType: PaceSessionType.Final)
            ]);
        var lowConfidence = calculator.CalculatePace(
            "target",
            [
                SessionResult(94, 0.10, SessionWeightFor(PaceSessionType.Final), sessionType: PaceSessionType.Final),
                SessionResult(72, 1.0, SessionWeightFor(PaceSessionType.Final), sessionType: PaceSessionType.Final)
            ]);

        Assert.True(highConfidence.Pace.HasValue);
        Assert.True(lowConfidence.Pace.HasValue);
        Assert.True(highConfidence.RawPace.HasValue);
        Assert.True(lowConfidence.RawPace.HasValue);
        Assert.Equal(highConfidence.Pace.Value, lowConfidence.Pace.Value, precision: 10);
        Assert.Equal(highConfidence.RawPace.Value, lowConfidence.RawPace.Value, precision: 10);
        Assert.True(highConfidence.Confidence > lowConfidence.Confidence);
    }

    [Fact]
    public void PartialFinalAddsLessFinalEvidenceThanCompleteFinal()
    {
        var calculator = new DownhillPaceCalculator();
        var partialSession = Find(calculator.CalculateSession(ConfidenceField(
            PaceSessionType.Final,
            sectorCount: 4,
            fieldSize: 30,
            targetValidSectorCount: 2)), "target");
        var completeSession = Find(calculator.CalculateSession(ConfidenceField(
            PaceSessionType.Final,
            sectorCount: 4,
            fieldSize: 30)), "target");
        var partial = calculator.CalculatePace("target", [partialSession]);
        var complete = calculator.CalculatePace("target", [completeSession]);

        Assert.True(complete.FinalEvidence > partial.FinalEvidence);
        Assert.True(complete.Confidence > partial.Confidence);
    }

    [Fact]
    public void PaceValueDoesNotChangeConfidenceForIdenticalEvidence()
    {
        var calculator = new DownhillPaceCalculator();
        var slow = calculator.CalculatePace("target", [SessionResult(
            30, 1, SessionWeightFor(PaceSessionType.Final),
            sessionType: PaceSessionType.Final)]);
        var fast = calculator.CalculatePace("target", [SessionResult(
            99, 1, SessionWeightFor(PaceSessionType.Final),
            sessionType: PaceSessionType.Final)]);

        Assert.Equal(slow.Confidence, fast.Confidence);
        Assert.Equal(slow.TotalEvidence, fast.TotalEvidence);
        Assert.Equal(slow.FinalEvidence, fast.FinalEvidence);
    }

    [Fact]
    public void ManySlowFinalsCanHaveHighConfidenceWhileOneFastQualifyingIsLow()
    {
        var calculator = new DownhillPaceCalculator();
        var manySlowFinals = calculator.CalculatePace(
            "target",
            Enumerable.Range(0, 6)
                .Select(_ => SessionResult(
                    30, 1, SessionWeightFor(PaceSessionType.Final),
                    sessionType: PaceSessionType.Final)));
        var oneFastQualifying = calculator.CalculatePace("target", [SessionResult(
            99, 1, SessionWeightFor(PaceSessionType.Qualifying),
            sessionType: PaceSessionType.Qualifying)]);

        Assert.Equal("High", manySlowFinals.ConfidenceStatus);
        Assert.Equal("Low", oneFastQualifying.ConfidenceStatus);
        Assert.True(manySlowFinals.Confidence > oneFastQualifying.Confidence);
    }

    [Theory]
    [InlineData(0, "Low")]
    [InlineData(44.999, "Low")]
    [InlineData(45, "Medium")]
    [InlineData(74.999, "Medium")]
    [InlineData(75, "High")]
    [InlineData(100, "High")]
    public void ConfidenceStatusUsesDocumentedThresholds(double confidence, string expected)
    {
        Assert.Equal(expected, DownhillPaceCalculator.GetConfidenceStatus(confidence));
    }

    [Fact]
    public void InsufficientFieldDataDoesNotCreateNeutralSectorScore()
    {
        var session = new PaceSessionData(
            PaceSessionType.Qualifying,
            [
                Run("target", [40, 50]),
                Run("rider-2", [41, 51])
            ]);
        var result = Find(
            new DownhillPaceCalculator().CalculateSession(session),
            "target");

        Assert.All(result.Sectors, sector => Assert.Null(sector.Score));
        Assert.Null(result.Pace);
        Assert.Equal("InsufficientSectorEvidence", result.NoResultReason);
    }

    [Fact]
    public void SlowRunFromTheStartIsNotClassifiedAsIncident()
    {
        var session = RankedTargetField(
            PaceSessionType.Qualifying,
            ranks: [80, 100, 90, 85, 80],
            targetTimes: [50, 60, 70, 80, 90],
            targetFinishRank: 95);
        var result = Find(
            new DownhillPaceCalculator().CalculateSession(session),
            "target");

        Assert.Null(result.IncidentSectorIndex);
        Assert.All(result.Sectors, sector =>
            Assert.Equal(PaceSectorStatus.Valid, sector.Status));
    }

    [Fact]
    public void WeakRankSequenceIsEvaluatedAsCompleteSlowRun()
    {
        var result = CalculateWeakCompleteSession();

        Assert.Null(result.IncidentSectorIndex);
        Assert.Equal(5, result.Sectors.Count(sector =>
            sector.Status == PaceSectorStatus.Valid && sector.Score.HasValue));
        Assert.True(result.Pace < 65);
    }

    [Fact]
    public void WeakRiderWithPartialDnfSessionStaysBelowSixtyFive()
    {
        var calculator = new DownhillPaceCalculator();
        var partialDnf = Find(calculator.CalculateSession(RankedTargetField(
            PaceSessionType.Qualifying,
            ranks: [21, 37, 1, 1, 1],
            targetTimes: [40, 50, null, null, null],
            targetFinishRank: null,
            targetIsClassified: false)), "target");
        var weakComplete = CalculateWeakCompleteSession(calculator);

        var result = calculator.CalculatePace("target", [partialDnf, weakComplete]);

        Assert.Equal(0.4, partialDnf.SectorCoverage, precision: 10);
        Assert.True(result.Pace < 65);
    }

    [Fact]
    public void EvidenceAdjustmentDoesNotRaiseWeakScoreToFifty()
    {
        var session = new PaceSessionData(
            PaceSessionType.Qualifying,
            [
                Run("target", [49, 59, null, null, null], isClassified: false),
                Run("rider-2", [40, 50, 60, 70, 80]),
                Run("rider-3", [41, 51, 61, 71, 81]),
                Run("rider-4", [42, 52, 62, 72, 82]),
                Run("rider-5", [43, 53, 63, 73, 83])
            ]);
        var result = Find(
            new DownhillPaceCalculator().CalculateSession(session),
            "target");

        Assert.True(result.SectorPace <= 50);
        Assert.Equal(result.SectorPace, result.EvidenceAdjustedSectorPace);
    }

    [Fact]
    public void ConfiguredSessionWinBonusDoesNotAffectPartialDnf()
    {
        var session = PartialFiveSectorField(targetFinishRank: 1);
        var withoutBonus = Find(new DownhillPaceCalculator(new PaceOptions
        {
            FinalWinBonus = 0
        }).CalculateSession(session), "target");
        var withBonus = Find(
            new DownhillPaceCalculator(new PaceOptions
            {
                FinalWinBonus = 100
            }).CalculateSession(session),
            "target");

        Assert.Equal(withoutBonus.Pace, withBonus.Pace);
    }

    [Fact]
    public void ConfiguredSessionWinBonusDoesNotAffectLowCoverageRun()
    {
        var session = LowCoverageClassifiedField();
        var withoutBonus = Find(new DownhillPaceCalculator(new PaceOptions
        {
            FinalWinBonus = 0
        }).CalculateSession(session), "target");
        var withBonus = Find(
            new DownhillPaceCalculator(new PaceOptions
            {
                FinalWinBonus = 100
            }).CalculateSession(session),
            "target");

        Assert.Equal(0.6, withBonus.SectorCoverage, precision: 10);
        Assert.Equal(withoutBonus.Pace, withBonus.Pace);
    }

    [Fact]
    public void SessionAndCombinedPaceRemainWithinZeroToOneHundred()
    {
        var calculator = new DownhillPaceCalculator();
        var session = Find(
            calculator.CalculateSession(EliteField(PaceSessionType.Final)),
            "target");
        var combined = calculator.CalculatePace("target", [session]);

        Assert.InRange(session.Pace!.Value, 0, 100);
        Assert.InRange(combined.Pace!.Value, 0, 100);
    }

    private static PaceSessionData SingleSectorField()
    {
        return new PaceSessionData(
            PaceSessionType.Final,
            Enumerable.Range(0, 10)
                .Select(index => Run(
                    $"rider-{index}",
                    [40 + index],
                    finishRank: index + 1))
                .ToArray());
    }

    private static PaceSessionData ConfidenceField(
        PaceSessionType sessionType,
        int sectorCount,
        int fieldSize,
        int? targetValidSectorCount = null)
    {
        var validTargetSectors = targetValidSectorCount ?? sectorCount;
        var runs = new List<PaceRiderRun>
        {
            Run(
                "target",
                Enumerable.Range(0, sectorCount)
                    .Select(index => index < validTargetSectors
                        ? (double?)(40 + index * 10)
                        : null)
                    .ToArray())
        };

        for (var rider = 1; rider < fieldSize; rider++)
        {
            runs.Add(Run(
                $"rider-{rider}",
                Enumerable.Range(0, sectorCount)
                    .Select(index => (double?)(40 + index * 10 + rider * 0.1))
                    .ToArray()));
        }

        return new PaceSessionData(sessionType, runs);
    }

    private static RiderPaceResult OverallConfidenceForFinalCount(
        DownhillPaceCalculator calculator,
        int finalCount)
    {
        return calculator.CalculatePace(
            "target",
            Enumerable.Range(0, finalCount)
                .Select(_ => SessionResult(
                    80,
                    1,
                    SessionWeightFor(PaceSessionType.Final),
                    sessionType: PaceSessionType.Final)));
    }

    private static PaceSessionData MultiSectorField(IReadOnlyList<double?> targetTimes)
    {
        return new PaceSessionData(
            PaceSessionType.Final,
            [
                Run("target", targetTimes, finishRank: 1),
                Run("rider-2", [42, 52, 62, 72, 82], finishRank: 2),
                Run("rider-3", [44, 54, 64, 74, 84], finishRank: 3),
                Run("rider-4", [46, 56, 66, 76, 86], finishRank: 4),
                Run("rider-5", [48, 58, 68, 78, 88], finishRank: 5)
            ]);
    }

    private static PaceSessionData PartialFiveSectorField(int? targetFinishRank = null)
    {
        return new PaceSessionData(
            PaceSessionType.Final,
            [
                Run(
                    "target",
                    [40, 50, null, null, null],
                    finishRank: targetFinishRank,
                    isClassified: false),
                Run("rider-2", [42, 52, 62, 72, 82], finishRank: 1),
                Run("rider-3", [44, 54, 64, 74, 84], finishRank: 2),
                Run("rider-4", [46, 56, 66, 76, 86], finishRank: 3),
                Run("rider-5", [48, 58, 68, 78, 88], finishRank: 4)
            ]);
    }

    private static PaceSessionData ThreeSectorField()
    {
        return new PaceSessionData(
            PaceSessionType.Qualifying,
            [
                Run("target", [40, 54, 68], finishRank: 2),
                Run("rider-2", [42, 52, 62], finishRank: 1),
                Run("rider-3", [44, 56, 64], finishRank: 3),
                Run("rider-4", [46, 58, 70], finishRank: 4),
                Run("rider-5", [48, 60, 72], finishRank: 5)
            ]);
    }

    private static PaceSessionData LowCoverageClassifiedField()
    {
        return new PaceSessionData(
            PaceSessionType.Final,
            [
                Run("target", [40, 50, 60, 70, 80], finishRank: 1),
                Run("rider-2", [42, 52, 62, null, null], finishRank: 2),
                Run("rider-3", [44, 54, 64, null, null], finishRank: 3),
                Run("rider-4", [46, 56, 66, null, null], finishRank: 4),
                Run("rider-5", [48, 58, 68, null, null], finishRank: 5)
            ]);
    }

    private static PaceSessionData EliteField(
        PaceSessionType sessionType,
        double targetOffset = 0,
        int targetFinishRank = 1)
    {
        return new PaceSessionData(
            sessionType,
            [
                Run(
                    "target",
                    [
                        40 + targetOffset,
                        50 + targetOffset,
                        60 + targetOffset,
                        70 + targetOffset,
                        80 + targetOffset
                    ],
                    topSpeed: 65 - targetOffset,
                    finishRank: targetFinishRank),
                Run("rider-2", [40.2, 50.2, 60.2, 70.2, 80.2], topSpeed: 64.8, finishRank: 2),
                Run("rider-3", [40.4, 50.4, 60.4, 70.4, 80.4], topSpeed: 64.6, finishRank: 3),
                Run("rider-4", [40.6, 50.6, 60.6, 70.6, 80.6], topSpeed: 64.4, finishRank: 4),
                Run("rider-5", [40.8, 50.8, 60.8, 70.8, 80.8], topSpeed: 64.2, finishRank: 5)
            ]);
    }

    private static PaceSessionData RankedTargetField(
        PaceSessionType sessionType,
        IReadOnlyList<int> ranks,
        IReadOnlyList<double?> targetTimes,
        int? targetFinishRank,
        bool targetIsClassified = true)
    {
        if (ranks.Count != targetTimes.Count)
        {
            throw new ArgumentException("Ranks and target times must have equal lengths.");
        }

        var runs = new List<PaceRiderRun>
        {
            Run(
                "target",
                targetTimes,
                finishRank: targetFinishRank,
                isClassified: targetIsClassified)
        };

        for (var competitor = 1; competitor < 110; competitor++)
        {
            var sectorTimes = targetTimes
                .Select((targetTime, sectorIndex) =>
                {
                    if (!targetTime.HasValue)
                    {
                        return (double?)(35 + sectorIndex * 10 + competitor * 0.02);
                    }

                    var rank = ranks[sectorIndex];
                    if (competitor < rank)
                    {
                        var fraction = (double)competitor / rank;
                        return targetTime.Value * (0.70 + 0.29 * fraction);
                    }

                    return targetTime.Value *
                        (1.0 + 0.002 * (competitor - rank + 1));
                })
                .ToArray();
            runs.Add(Run(
                $"rider-{competitor}",
                sectorTimes,
                finishRank: competitor + 1));
        }

        return new PaceSessionData(sessionType, runs);
    }

    private static RiderSessionPaceResult CalculateWeakCompleteSession(
        DownhillPaceCalculator? calculator = null)
    {
        calculator ??= new DownhillPaceCalculator();
        var session = RankedTargetField(
            PaceSessionType.Qualifying,
            ranks: [31, 103, 88, 65, 80],
            targetTimes: [40, 135, 45, 46, 45.335],
            targetFinishRank: 100);
        return Find(calculator.CalculateSession(session), "target");
    }

    private static double SessionWeightFor(PaceSessionType sessionType)
    {
        var session = SingleSectorField() with { SessionType = sessionType };
        return Find(
            new DownhillPaceCalculator().CalculateSession(session),
            "rider-0").SessionWeight;
    }

    private static RiderSessionPaceResult SessionResult(
        double pace,
        double confidence,
        double sessionWeight,
        double sectorCoverage = 1,
        PaceSessionType sessionType = PaceSessionType.Qualifying,
        int usedSectorCount = 5,
        int? competitionClassPriority = null)
    {
        var sectors = Enumerable.Range(0, usedSectorCount)
            .Select(index => new PaceSectorResult(
                SectorIndex: index,
                Status: PaceSectorStatus.Valid,
                Time: 1,
                Rank: 1,
                Percentile: 1,
                PerformanceZ: 1,
                Score: pace))
            .ToArray();

        return new RiderSessionPaceResult(
            RiderId: "target",
            SessionType: sessionType,
            Pace: pace,
            SectorPace: pace,
            SpeedScore: null,
            FullRunTimeScore: null,
            Confidence: confidence * 100.0,
            SessionWeight: sessionWeight,
            IsRepresentative: true,
            IncidentSectorIndex: null,
            Sectors: sectors,
            SectorCoverage: sectorCoverage,
            EvidenceAdjustedSectorPace: pace,
            CompetitionClassPriority: competitionClassPriority);
    }

    private static StagePaceResult Stage(double pace, double weight) =>
        new(pace, EvidenceWeight: 1, RoundWeight: weight, UsedSessions: 1);

    private static PaceRiderRun Run(
        string riderId,
        IReadOnlyList<double?> sectorTimes,
        double? topSpeed = null,
        int? finishRank = null,
        double? finishTimeSeconds = null,
        bool isClassified = true,
        string? status = null)
    {
        return new PaceRiderRun(
            RiderId: riderId,
            SectorTimes: sectorTimes,
            TopSpeed: topSpeed,
            FinishRank: finishRank,
            FinishTimeSeconds: finishTimeSeconds,
            IsClassified: isClassified,
            Status: status);
    }

    private static PaceFieldSummary Summary(params double[] input)
    {
        var values = input.Order().ToArray();
        var median = Median(values);
        var logs = values.Select(value => Math.Log(value)).ToArray();
        var logMedian = Median(logs);
        var logMad = Median(logs.Select(value => Math.Abs(value - logMedian)).ToArray());
        var linearMad = Median(values.Select(value => Math.Abs(value - median)).ToArray());
        return new PaceFieldSummary(
            values.Length,
            median,
            logMedian,
            Math.Max(1.4826 * logMad, 0.003),
            Math.Max(1.4826 * linearMad, Math.Max(median * 0.003, 0.01)),
            values[0],
            values.Length > 1 ? values[1] : null);
    }

    private static double Median(double[] values)
    {
        var middle = values.Length / 2;
        return values.Length % 2 == 1
            ? values[middle]
            : (values[middle - 1] + values[middle]) / 2.0;
    }

    private static RiderSessionPaceResult Find(
        IReadOnlyList<RiderSessionPaceResult> results,
        string riderId)
    {
        return results.Single(result => result.RiderId == riderId);
    }
}
