using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class DownhillConsistencyCalculatorTests
{
    [Fact]
    public void ReliabilityCalibrationIsCentralizedAndSumsToOne()
    {
        var options = new ConsistencyOptions();

        Assert.Equal(0.40, options.IncidentReliabilityWeight, 10);
        Assert.Equal(0.35, options.FinalQualificationWeight, 10);
        Assert.Equal(0.05, options.QualificationQualityWeight, 10);
        Assert.Equal(0.20, options.PositionRepeatabilityWeight, 10);
        Assert.Equal(
            1,
            options.IncidentReliabilityWeight
                + options.FinalQualificationWeight
                + options.QualificationQualityWeight
                + options.PositionRepeatabilityWeight,
            10
        );
    }

    [Theory]
    [InlineData(nameof(PaceSessionType.Final), 4.00)]
    [InlineData(nameof(PaceSessionType.SemiFinal), 2.00)]
    [InlineData(nameof(PaceSessionType.Qualifying), 1.50)]
    [InlineData(nameof(PaceSessionType.Qualifying1), 1.50)]
    [InlineData(nameof(PaceSessionType.Qualifying2), 1.50)]
    [InlineData(nameof(PaceSessionType.Seeding), 0.60)]
    [InlineData(nameof(PaceSessionType.TimedTraining), 0.00)]
    [InlineData(nameof(PaceSessionType.Training), 0.00)]
    public void UsesCentralSessionWeights(string sessionTypeName, double expected)
    {
        var sessionType = Enum.Parse<PaceSessionType>(sessionTypeName);
        Assert.Equal(
            expected,
            DownhillConsistencyCalculator.GetConsistencySessionWeight(sessionType),
            10
        );
    }

    [Fact]
    public void RiderWithoutIncidentsScoresHigherThanFrequentIncidentRider()
    {
        var calculator = new DownhillConsistencyCalculator();
        var clean = calculator.Calculate(Weekend("a", 4).Concat(Weekend("b", 5)).ToArray());
        var incidentProne = calculator.Calculate(
            Weekend("a", 4, finalStatus: "DNF")
                .Concat(Weekend("b", 5, finalOutlier: true))
                .ToArray()
        );

        Assert.True(clean.Consistency > incidentProne.Consistency);
        Assert.Equal(100, clean.IncidentReliability);
        Assert.True(incidentProne.IncidentReliability < clean.IncidentReliability);
    }

    [Fact]
    public void FullFinalQualificationScoresHigherThanHalfQualification()
    {
        var calculator = new DownhillConsistencyCalculator();
        var full = calculator.Calculate(Weekend("a", 4).Concat(Weekend("b", 4)).ToArray());
        var half = calculator.Calculate(
            Weekend("a", 4).Concat([Run("b-q1", "b", PaceSessionType.Qualifying1, 4)]).ToArray()
        );

        Assert.Equal(100, full.FinalQualification);
        Assert.Equal(50, half.FinalQualification);
        Assert.True(full.Consistency > half.Consistency);
    }

    [Fact]
    public void DirectQ1QualificationScoresHigherThanQ2Qualification()
    {
        var calculator = new DownhillConsistencyCalculator();
        var direct = calculator.Calculate(Weekend("a", 4));
        var throughQ2 = calculator.Calculate([
            Run("a-q1", "a", PaceSessionType.Qualifying1, 4),
            Run("a-q2", "a", PaceSessionType.Qualifying2, 4),
            Run("a-final", "a", PaceSessionType.Final, 4),
        ]);

        Assert.Equal(100, direct.QualificationQuality);
        Assert.Equal(75, throughQ2.QualificationQuality);
        Assert.True(direct.Consistency > throughQ2.Consistency);
    }

    [Fact]
    public void Q2QualificationIsMuchBetterThanMissingFinal()
    {
        var calculator = new DownhillConsistencyCalculator();
        var throughQ2 = calculator.Calculate([
            Run("a-q1", "a", PaceSessionType.Qualifying1, 4),
            Run("a-q2", "a", PaceSessionType.Qualifying2, 4),
            Run("a-final", "a", PaceSessionType.Final, 4),
        ]);
        var missedFinal = calculator.Calculate([
            Run("a-q1", "a", PaceSessionType.Qualifying1, 4),
            Run("a-q2", "a", PaceSessionType.Qualifying2, 4),
        ]);

        Assert.True(throughQ2.Consistency - missedFinal.Consistency > 25);
        Assert.Equal(0, missedFinal.FinalQualification);
        Assert.Equal(0, missedFinal.QualificationQuality);
    }

    [Fact]
    public void RepeatableCompletedPositionsCannotMaskFrequentCrashes()
    {
        var result = new DownhillConsistencyCalculator().Calculate(
            Weekend("a", 4)
                .Concat(Weekend("b", 4, finalStatus: "DNF"))
                .Concat(Weekend("c", 4, finalStatus: "DNF"))
                .ToArray()
        );

        Assert.Equal(100, result.PositionRepeatability);
        Assert.True(result.IncidentReliability < 60);
        Assert.True(result.Consistency < 85);
    }

    [Fact]
    public void StableP1AndStableP80HaveEqualPositionRepeatability()
    {
        var calculator = new DownhillConsistencyCalculator();
        var first = calculator.Calculate(Weekend("a", 1).Concat(Weekend("b", 1)).ToArray());
        var last = calculator.Calculate(Weekend("a", 80).Concat(Weekend("b", 80)).ToArray());

        Assert.Equal(100, first.PositionRepeatability);
        Assert.Equal(first.PositionRepeatability, last.PositionRepeatability);
        Assert.Equal(first.Consistency, last.Consistency);
    }

    [Fact]
    public void DsqAndDnsAreIgnoredAsSportingConsistencyEvidence()
    {
        var calculator = new DownhillConsistencyCalculator();
        var baselineRuns = Weekend("a", 4).Concat(Weekend("b", 5)).ToArray();
        var baseline = calculator.Calculate(baselineRuns);
        var withIgnoredStatuses = calculator.Calculate([
            .. baselineRuns,
            Run("c-dsq", "c", PaceSessionType.Final, null, status: "DSQ"),
            Run("d-dns", "d", PaceSessionType.Final, null, status: "DNS"),
        ]);

        Assert.Equal(baseline.Consistency, withIgnoredStatuses.Consistency);
        Assert.Equal(baseline.UsedRuns, withIgnoredStatuses.UsedRuns);
    }

    [Fact]
    public void DetectedSectorIncidentCountsOncePerRun()
    {
        var result = new DownhillConsistencyCalculator().Calculate([
            Run("a-q1", "a", PaceSessionType.Qualifying1, 4),
            Run("a-final", "a", PaceSessionType.Final, 4, outlier: true),
        ]);

        Assert.Equal(
            0.85 * 100 * Math.Pow(2.0 / 3.0, 1.2) + 0.15 * 100 * Math.Pow(0.75, 1.2),
            result.IncidentReliability!.Value,
            10
        );
    }

    [Fact]
    public void StatisticalSectorOutlierIsNotAutomaticallyAnIncident()
    {
        var result = new DownhillConsistencyCalculator().Calculate([
            new ConsistencyRunInput(
                "a-q1",
                "a",
                PaceSessionType.Qualifying1,
                [new ConsistencySectorTimeInput("sector:0", 30)]
            ),
            new ConsistencyRunInput(
                "a-final",
                "a",
                PaceSessionType.Final,
                [new ConsistencySectorTimeInput("sector:0", 45, IsOutlier: true)]
            ),
        ]);

        Assert.Equal(100, result.IncidentReliability);
    }

    [Fact]
    public void OneIncidentDoesNotCollapseAnOtherwiseCompleteWeekend()
    {
        var result = new DownhillConsistencyCalculator().Calculate(
            Weekend("a", 4, finalOutlier: true)
        );

        Assert.True(result.Consistency >= 70);
        Assert.Equal(100, result.FinalQualification);
    }

    [Fact]
    public void FinalIncidentsAndQualificationRouteDetermineTheExpectedOrdering()
    {
        var calculator = new DownhillConsistencyCalculator();
        var pierron = calculator.Calculate(
            Enumerable
                .Range(1, 7)
                .SelectMany(index =>
                    Weekend($"p-{index}", 4, finalStatus: index <= 4 ? "DNF" : null)
                )
                .ToArray()
        );
        var vermette = calculator.Calculate(
            Enumerable
                .Range(1, 7)
                .SelectMany(index =>
                    index <= 3
                        ? WeekendThroughQ2($"v-{index}", 5, index <= 2)
                        : Weekend($"v-{index}", 5)
                )
                .ToArray()
        );
        var vergier = calculator.Calculate(
            Enumerable
                .Range(1, 7)
                .SelectMany(index =>
                    Weekend($"l-{index}", 3, finalStatus: index == 1 ? "DNF" : null)
                )
                .ToArray()
        );

        Assert.True(pierron.Consistency < vermette.Consistency);
        Assert.True(vermette.Consistency < vergier.Consistency);
        Assert.True(vergier.Consistency > 85);
    }

    [Fact]
    public void RepeatedIncidentsHaveAProgressivelyStrongerPenalty()
    {
        var calculator = new DownhillConsistencyCalculator();
        var oneIncident = calculator.Calculate(
            Enumerable
                .Range(1, 7)
                .SelectMany(index =>
                    Weekend($"one-{index}", 4, finalStatus: index == 1 ? "DNF" : null)
                )
                .ToArray()
        );
        var twoIncidents = calculator.Calculate(
            Enumerable
                .Range(1, 7)
                .SelectMany(index =>
                    Weekend($"two-{index}", 4, finalStatus: index <= 2 ? "DNF" : null)
                )
                .ToArray()
        );
        var fourIncidents = calculator.Calculate(
            Enumerable
                .Range(1, 7)
                .SelectMany(index =>
                    Weekend($"four-{index}", 4, finalStatus: index <= 4 ? "DNF" : null)
                )
                .ToArray()
        );

        var firstAdditionalLoss = oneIncident.Consistency!.Value - twoIncidents.Consistency!.Value;
        var laterAdditionalLoss = twoIncidents.Consistency.Value - fourIncidents.Consistency!.Value;

        Assert.True(oneIncident.Consistency > 90);
        Assert.True(twoIncidents.Consistency > fourIncidents.Consistency);
        Assert.True(laterAdditionalLoss > firstAdditionalLoss);
        Assert.True(fourIncidents.Consistency < 75);
    }

    [Fact]
    public void RepeatedIncidentPenaltyDependsOnFrequencyNotCareerCount()
    {
        var calculator = new DownhillConsistencyCalculator();
        var concentrated = calculator.Calculate(
            Enumerable
                .Range(1, 7)
                .SelectMany(index =>
                    Weekend($"short-{index}", 4, finalStatus: index <= 4 ? "DNF" : null)
                )
                .ToArray()
        );
        var occasional = calculator.Calculate(
            Enumerable
                .Range(1, 35)
                .SelectMany(index =>
                    Weekend($"long-{index}", 4, finalStatus: index <= 4 ? "DNF" : null)
                )
                .ToArray()
        );

        Assert.True(occasional.IncidentReliability > concentrated.IncidentReliability);
        Assert.True(occasional.Consistency > concentrated.Consistency + 10);
    }

    [Fact]
    public void FinalPositionVariationMattersMoreThanQualifyingVariation()
    {
        var calculator = new DownhillConsistencyCalculator();
        var variableQualifying = calculator.Calculate([
            Run("a-q1", "a", PaceSessionType.Qualifying1, 40),
            Run("a-final", "a", PaceSessionType.Final, 4),
            Run("b-q1", "b", PaceSessionType.Qualifying1, 1),
            Run("b-final", "b", PaceSessionType.Final, 4),
        ]);
        var variableFinals = calculator.Calculate([
            Run("a-q1", "a", PaceSessionType.Qualifying1, 4),
            Run("a-final", "a", PaceSessionType.Final, 40),
            Run("b-q1", "b", PaceSessionType.Qualifying1, 4),
            Run("b-final", "b", PaceSessionType.Final, 1),
        ]);

        Assert.True(
            variableQualifying.PositionRepeatability > variableFinals.PositionRepeatability
        );
        Assert.True(variableQualifying.Consistency > variableFinals.Consistency);
    }

    [Fact]
    public void SectorAnalysisStillDetectsLargeWithinRunDrops()
    {
        var sectors = new[] { 0.95, 0.94, 0.10, 0.96 }
            .Select(
                (percentile, index) =>
                    new PaceSectorResult(
                        index,
                        PaceSectorStatus.Valid,
                        30,
                        null,
                        percentile,
                        null,
                        100 * percentile
                    )
            )
            .ToArray();

        var profile = new DownhillConsistencyCalculator().AnalyzeSectors(sectors);

        Assert.Single(profile.SectorIndexes);
        Assert.Contains(2, profile.SectorIndexes);
    }

    [Fact]
    public void ResultAlwaysStaysWithinZeroToOneHundred()
    {
        var result = new DownhillConsistencyCalculator().Calculate([
            Run("a-q1", "a", PaceSessionType.Qualifying1, 1, 100),
            Run("a-final", "a", PaceSessionType.Final, null, 100, "DNF", true),
            Run("b-q1", "b", PaceSessionType.Qualifying1, 100, 100),
        ]);

        Assert.InRange(result.Consistency!.Value, 0, 100);
        Assert.InRange(result.IncidentReliability!.Value, 0, 100);
        Assert.InRange(result.FinalQualification!.Value, 0, 100);
        Assert.InRange(result.QualificationQuality!.Value, 0, 100);
        Assert.InRange(result.PositionRepeatability!.Value, 0, 100);
    }

    private static ConsistencyRunInput[] Weekend(
        string eventId,
        int position,
        int fieldSize = 80,
        string? finalStatus = null,
        bool finalOutlier = false
    ) =>
        [
            Run($"{eventId}-q1", eventId, PaceSessionType.Qualifying1, position, fieldSize),
            Run(
                $"{eventId}-final",
                eventId,
                PaceSessionType.Final,
                finalStatus is null ? position : null,
                fieldSize,
                finalStatus,
                finalOutlier
            ),
        ];

    private static ConsistencyRunInput[] WeekendThroughQ2(
        string eventId,
        int position,
        bool finalIncident
    ) =>
        [
            Run($"{eventId}-q1", eventId, PaceSessionType.Qualifying1, position),
            Run($"{eventId}-q2", eventId, PaceSessionType.Qualifying2, position),
            Run(
                $"{eventId}-final",
                eventId,
                PaceSessionType.Final,
                finalIncident ? null : position,
                status: finalIncident ? "DNF" : null
            ),
        ];

    private static ConsistencyRunInput Run(
        string runId,
        string eventId,
        PaceSessionType sessionType,
        int? position,
        int fieldSize = 80,
        string? status = null,
        bool outlier = false
    ) =>
        new(
            runId,
            eventId,
            sessionType,
            [new ConsistencySectorTimeInput("sector:0", position.HasValue ? 30 : null, outlier)],
            position,
            fieldSize,
            position.HasValue,
            Status: status,
            HasIncident: outlier
        );
}
