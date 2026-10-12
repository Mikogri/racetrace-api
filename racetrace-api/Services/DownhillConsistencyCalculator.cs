using RaceTrace.Api.Infrastructure.Calculations;
using RaceTrace.Api.Infrastructure.Telemetry;

namespace RaceTrace.Api.Services;

internal sealed partial class DownhillConsistencyCalculator
{
    private readonly ConsistencyOptions _options;
    private readonly SessionWeightOptions _sessionWeights;

    public DownhillConsistencyCalculator(
        ConsistencyOptions? options = null,
        SessionWeightOptions? sessionWeights = null)
    {
        _options = options ?? AlgorithmConfiguration.Consistency;
        _sessionWeights = sessionWeights ?? AlgorithmConfiguration.SessionWeights;
        ValidateOptions(_options);
    }

    private static void ValidateOptions(ConsistencyOptions options)
    {
        if (options.VariationAtHalfScore <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "VariationAtHalfScore must be greater than zero.");
        }

        if (options.IncidentReliabilityWeight < 0 ||
            options.FinalQualificationWeight < 0 ||
            options.QualificationQualityWeight < 0 ||
            options.PositionRepeatabilityWeight < 0 ||
            Math.Abs(
                options.IncidentReliabilityWeight +
                options.FinalQualificationWeight +
                options.QualificationQualityWeight +
                options.PositionRepeatabilityWeight - 1.0) > 0.000001)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Consistency component weights must be non-negative and sum to 1.");
        }

        if (options.IncidentRateExponent <= 0 ||
            options.IncidentToleranceRuns < 0 ||
            options.MaximumRepeatedIncidentPenalty < 0 ||
            options.RepeatedIncidentPenaltyExponent <= 0 ||
            options.Q2QualificationScore is < 0 or > 100 ||
            options.FinalIncidentReliabilityShare is < 0 or > 1 ||
            options.PositionVariationAtHalfScore <= 0 ||
            options.RobustPositionDeviationWeight is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Consistency reliability calibration is outside its valid range.");
        }

        if (options.OutlierReferenceQuantile is <= 0 or > 1 ||
            options.MinimumOutlierReferencePercentile is < 0 or > 1 ||
            options.OutlierPercentileDropThreshold is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Consistency outlier detection options are outside their valid ranges.");
        }
    }

    internal ConsistencyOutlierProfile AnalyzeSectors(
        IReadOnlyList<PaceSectorResult> sectors)
    {
        var candidates = sectors
            .Where(sector =>
                sector.Status != PaceSectorStatus.InvalidTiming &&
                sector.Time.HasValue &&
                sector.Percentile.HasValue &&
                IsValidTime(sector.Time.Value))
            .ToArray();
        if (candidates.Length < 2)
        {
            return new ConsistencyOutlierProfile(
                new HashSet<int>(),
                null);
        }

        var orderedCandidates = candidates.OrderBy(sector => sector.Percentile!.Value).ToArray();
        var referencePercentile = QuantileSorted(
            orderedCandidates.Select(sector => sector.Percentile!.Value).ToArray(),
            _options.OutlierReferenceQuantile);
        if (referencePercentile <
            _options.MinimumOutlierReferencePercentile)
        {
            return new ConsistencyOutlierProfile(
                new HashSet<int>(),
                null);
        }

        var outlierIndexes = candidates
            .Where(sector =>
                sector.Status == PaceSectorStatus.Incident ||
                referencePercentile - sector.Percentile!.Value >=
                    _options.OutlierPercentileDropThreshold)
            .Select(sector => sector.SectorIndex)
            .ToHashSet();
        if (outlierIndexes.Count == 0)
        {
            return new ConsistencyOutlierProfile(
                outlierIndexes,
                null);
        }

        var cleanPercentiles = orderedCandidates
            .Where(sector => !outlierIndexes.Contains(sector.SectorIndex))
            .Select(sector => sector.Percentile!.Value)
            .ToArray();
        return new ConsistencyOutlierProfile(
            outlierIndexes,
            cleanPercentiles.Length == 0
                ? null
                : 100.0 * QuantileSorted(cleanPercentiles, 0.50));
    }

    public RiderConsistencyResult Calculate(
        IReadOnlyList<ConsistencyRunInput> runs,
        CompetitionClassWeighting? competitionClassWeighting = null)
    {
        using var measurement = ApiTelemetry.Measure("consistency");
        var officialRuns = runs
            .Where(run => _sessionWeights.GetWeight(run.SessionType) > 0)
            .ToArray();
        var relevantRuns = officialRuns
            .Where(run => !IsIgnoredSportingStatus(run.Status))
            .ToArray();

        if (relevantRuns.Length < 2)
        {
            return EmptyResult(relevantRuns.Length);
        }

        var sectorResults = new List<SectorConsistencyResult>();
        var totalComparisonOpportunities = 0;

        foreach (var courseGroup in relevantRuns.GroupBy(
                     run => run.CourseKey,
                     StringComparer.OrdinalIgnoreCase))
        {
            var courseRuns = courseGroup.ToArray();
            if (courseRuns.Length < 2)
            {
                continue;
            }

            var expectedSectorKeys = courseRuns
                .SelectMany(run => run.Sectors)
                .Select(sector => sector.SectorKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // Lookup preserves first-match semantics, case-insensitive and even null keys.
            var indexedRuns = courseRuns.Select(run => (
                Weight: _sessionWeights.GetWeight(run.SessionType),
                Sectors: run.Sectors.ToLookup(sector => sector.SectorKey, StringComparer.OrdinalIgnoreCase)))
                .ToArray();

            foreach (var sectorKey in expectedSectorKeys)
            {
                var attemptCount = 0;
                var weightedTimes = new List<WeightedTime>(courseRuns.Length);
                double totalAttemptWeight = 0, completedWeight = 0;
                foreach (var run in indexedRuns)
                {
                    var sector = run.Sectors[sectorKey].FirstOrDefault();
                    if (sector?.IsInvalidTiming == true) continue;
                    attemptCount++;
                    totalAttemptWeight += run.Weight;
                    if (sector?.TimeSeconds is { } time && IsValidTime(time))
                    {
                        weightedTimes.Add(new WeightedTime(time, run.Weight));
                        completedWeight += run.Weight;
                    }
                }
                if (attemptCount < 2) continue;
                var completionRate = totalAttemptWeight > 0
                    ? completedWeight / totalAttemptWeight
                    : 0;
                double? relativeVariation = null;
                var timeConsistency = 0.0;

                if (weightedTimes.Count >= 2)
                {
                    relativeVariation = CalculateWeightedRelativeVariation(weightedTimes);
                    timeConsistency = VariationToScore(relativeVariation.Value);
                }

                var rawSectorScore = Clamp(
                    timeConsistency * completionRate,
                    0,
                    100);
                sectorResults.Add(new SectorConsistencyResult(
                    courseGroup.Key,
                    sectorKey,
                    rawSectorScore,
                    relativeVariation,
                    attemptCount,
                    weightedTimes.Count));

                totalComparisonOpportunities +=
                    attemptCount * (attemptCount - 1) / 2;
            }
        }

        var eventGroups = officialRuns
            .GroupBy(run => run.CourseKey, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Any(run => !IsIgnoredSportingStatus(run.Status)))
            .Select(group => group.ToArray())
            .ToArray();
        var incidentReliability = CalculateIncidentReliability(relevantRuns);
        var (finalQualification, qualificationQuality) = CalculateQualification(eventGroups);
        var positionRepeatability = CalculatePositionRepeatability(relevantRuns);
        var consistency =
            _options.IncidentReliabilityWeight * incidentReliability +
            _options.FinalQualificationWeight * finalQualification +
            _options.QualificationQualityWeight * qualificationQuality +
            _options.PositionRepeatabilityWeight * positionRepeatability;

        var confidence = 1.0 - Math.Exp(
            -(totalComparisonOpportunities + relevantRuns.Length + eventGroups.Length) /
            _options.ConfidenceComparisonScale);

        return new RiderConsistencyResult(
            Consistency: Clamp(consistency, 0, 100),
            Confidence: Clamp(confidence, 0, 1),
            UsedRuns: relevantRuns.Length,
            UsedSectors: sectorResults.Count,
            Sectors: sectorResults)
        {
            IncidentReliability = incidentReliability,
            FinalQualification = finalQualification,
            QualificationQuality = qualificationQuality,
            PositionRepeatability = positionRepeatability
        };
    }

    private double CalculateIncidentReliability(
        IReadOnlyCollection<ConsistencyRunInput> runs)
    {
        if (runs.Count == 0)
        {
            return 0;
        }

        var allRunReliability = IncidentReliabilityScore(runs);
        var finalRuns = runs
            .Where(run => run.SessionType == PaceSessionType.Final)
            .ToArray();
        if (finalRuns.Length == 0)
        {
            return allRunReliability;
        }

        var finalReliability = IncidentReliabilityScore(finalRuns);
        return
            _options.FinalIncidentReliabilityShare * finalReliability +
            (1.0 - _options.FinalIncidentReliabilityShare) * allRunReliability;
    }

    private double IncidentReliabilityScore(
        IReadOnlyCollection<ConsistencyRunInput> runs)
    {
        var incidentCount = runs.Count(IsIncidentRun);
        var cleanRate = 1.0 - incidentCount /
            (runs.Count + _options.IncidentToleranceRuns);
        var baseReliability = 100.0 * Math.Pow(
            Clamp(cleanRate, 0, 1),
            _options.IncidentRateExponent);
        var repeatedIncidentRate = incidentCount <= 1
            ? 0
            : (incidentCount - 1.0) /
                (runs.Count + _options.IncidentToleranceRuns);
        var repeatedIncidentPenalty =
            _options.MaximumRepeatedIncidentPenalty * Math.Pow(
                repeatedIncidentRate,
                _options.RepeatedIncidentPenaltyExponent);
        return Clamp(baseReliability - repeatedIncidentPenalty, 0, 100);
    }

    private static bool IsIncidentRun(ConsistencyRunInput run) =>
        string.Equals(run.Status, "DNF", StringComparison.OrdinalIgnoreCase) ||
        run.HasIncident;

    private (double Final, double Quality) CalculateQualification(
        IReadOnlyCollection<ConsistencyRunInput[]> events)
    {
        if (events.Count == 0) return (0, 0);

        var qualified = 0;
        double qualitySum = 0;
        foreach (var runs in events)
        {
            if (!runs.Any(run => run.SessionType == PaceSessionType.Final)) continue;
            qualified++;
            qualitySum += runs.Any(run =>
                run.SessionType == PaceSessionType.Qualifying2 &&
                !IsIgnoredSportingStatus(run.Status))
                    ? _options.Q2QualificationScore
                    : 100.0;
        }
        return (100.0 * qualified / events.Count, qualitySum / events.Count);
    }

    private double CalculatePositionRepeatability(
        IReadOnlyCollection<ConsistencyRunInput> runs)
    {
        var eligibleRuns = runs
            .Where(run =>
                run.IsClassified &&
                run.FinishRank is > 0 &&
                run.FieldSize is > 0 &&
                run.FinishRank <= run.FieldSize)
            .ToArray();
        var sessionTypes = new PaceSessionType[eligibleRuns.Length];
        var weights = new double[eligibleRuns.Length];
        for (var index = 0; index < eligibleRuns.Length; index++)
        {
            sessionTypes[index] = eligibleRuns[index].SessionType;
            weights[index] = _sessionWeights.GetWeight(sessionTypes[index]);
        }
        var effectiveWeights = _sessionWeights.ApplyFinalTargetImpact(sessionTypes, weights);
        var positions = eligibleRuns
            .Select((run, index) => new WeightedPosition(
                run.FieldSize == 1
                    ? 0
                    : (run.FinishRank!.Value - 1.0) / (run.FieldSize!.Value - 1.0),
                effectiveWeights[index]))
            .Where(position => position.Weight > 0)
            .ToArray();
        if (positions.Length < 2)
        {
            return 50.0;
        }

        var center = WeightedMedian(positions);
        var deviations = positions
            .Select(position => new WeightedPosition(
                Math.Abs(position.Value - center),
                position.Weight))
            .ToArray();
        var robustDeviation = 1.4826 * WeightedMedian(deviations);
        var weightSum = deviations.Sum(value => value.Weight);
        var meanDeviation = deviations.Sum(value => value.Value * value.Weight) / weightSum;
        var blendedDeviation =
            _options.RobustPositionDeviationWeight * robustDeviation +
            (1.0 - _options.RobustPositionDeviationWeight) * meanDeviation;
        var normalized = blendedDeviation / _options.PositionVariationAtHalfScore;
        return 100.0 / (1.0 + normalized * normalized);
    }

    private static bool IsIgnoredSportingStatus(string? status) =>
        string.Equals(status, "DNS", StringComparison.OrdinalIgnoreCase) ||
        DownhillPaceCalculator.IsDisqualified(status);

    private double VariationToScore(double relativeVariation)
    {
        var normalized = relativeVariation / _options.VariationAtHalfScore;
        return 100.0 / (1.0 + normalized * normalized);
    }

    internal static double GetConsistencySessionWeight(PaceSessionType sessionType)
    {
        return DownhillPaceCalculator.GetSessionWeight(sessionType);
    }

    private static RiderConsistencyResult EmptyResult(int usedRuns = 0)
    {
        return new RiderConsistencyResult(
            Consistency: null,
            Confidence: 0,
            UsedRuns: usedRuns,
            UsedSectors: 0,
            Sectors: []);
    }

}
