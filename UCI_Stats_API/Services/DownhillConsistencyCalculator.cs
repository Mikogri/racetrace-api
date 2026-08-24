namespace UCI_Stats_API.Services;

internal sealed record ConsistencyRunInput(
    string RunId,
    string CourseKey,
    PaceSessionType SessionType,
    IReadOnlyList<ConsistencySectorTimeInput> Sectors,
    int? FinishRank = null,
    int? FieldSize = null,
    bool IsClassified = false,
    int? CompetitionClassPriority = null,
    double? ProjectedFinishPositionScore = null,
    string? Status = null,
    bool HasIncident = false);

internal sealed record ConsistencySectorTimeInput(
    string SectorKey,
    double? TimeSeconds,
    bool IsOutlier = false);

internal sealed record SectorConsistencyResult(
    string CourseKey,
    string SectorKey,
    double Consistency,
    double? RelativeVariation,
    int TotalRuns,
    int CompletedRuns);

internal sealed record RiderConsistencyResult(
    double? Consistency,
    double Confidence,
    int UsedRuns,
    int UsedSectors,
    IReadOnlyList<SectorConsistencyResult> Sectors)
{
    public double? IncidentReliability { get; init; }

    public double? FinalQualification { get; init; }

    public double? QualificationQuality { get; init; }

    public double? PositionRepeatability { get; init; }
}

internal sealed record ConsistencyOutlierProfile(
    IReadOnlySet<int> SectorIndexes,
    double? ProjectedFinishPositionScore);

internal sealed class ConsistencyOptions
{
    public double VariationAtHalfScore { get; init; } = 0.06;

    public double ConfidenceComparisonScale { get; init; } = 20.0;

    // Reliability is the primary signal: frequent incidents must dominate the score.
    public double IncidentReliabilityWeight { get; init; } = 0.40;

    // Reaching the final is the second-strongest measure of sporting reliability.
    public double FinalQualificationWeight { get; init; } = 0.35;

    // Direct Q1 qualification is preferred, while advancing through Q2 remains positive.
    public double QualificationQualityWeight { get; init; } = 0.05;

    // This component measures only position variation, not whether the position was good.
    public double PositionRepeatabilityWeight { get; init; } = 0.20;

    // Higher values make each incident reduce Incident Reliability more aggressively.
    public double IncidentRateExponent { get; init; } = 1.20;

    // Prevents one incident in a short sample from collapsing reliability to zero.
    // As evidence grows, the observed incident rate increasingly drives the score.
    public double IncidentToleranceRuns { get; init; } = 2.0;

    // Repeated incidents are increasingly meaningful. The first incident is
    // protected by sample tolerance; this penalty starts with the second one.
    public double RepeatedIncidentPenalty { get; init; } = 5.0;

    public double RepeatedIncidentPenaltyExponent { get; init; } = 1.30;

    // Score assigned to an event where the rider reached the final through Q2.
    public double Q2QualificationScore { get; init; } = 75.0;

    // Final incidents drive sporting reliability; incidents in earlier sessions
    // remain supporting evidence but cannot dilute repeated final crashes.
    public double FinalIncidentReliabilityShare { get; init; } = 0.85;

    // Robust normalized-position deviation that produces 50 Position Repeatability.
    public double PositionVariationAtHalfScore { get; init; } = 0.12;

    // Keeps the position signal resistant to one extreme result without hiding
    // repeatable smaller changes around the rider's typical position.
    public double RobustPositionDeviationWeight { get; init; } = 0.70;

    public double OutlierReferenceQuantile { get; init; } = 0.75;

    public double MinimumOutlierReferencePercentile { get; init; } = 0.60;

    public double OutlierPercentileDropThreshold { get; init; } = 0.35;
}

internal sealed class DownhillConsistencyCalculator
{
    private readonly ConsistencyOptions _options;
    private readonly SessionWeightOptions _sessionWeights;

    public DownhillConsistencyCalculator(
        ConsistencyOptions? options = null,
        SessionWeightOptions? sessionWeights = null)
    {
        _options = options ?? new ConsistencyOptions();
        _sessionWeights = sessionWeights ?? new SessionWeightOptions();
        if (_options.VariationAtHalfScore <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "VariationAtHalfScore must be greater than zero.");
        }

        if (_options.IncidentReliabilityWeight < 0 ||
            _options.FinalQualificationWeight < 0 ||
            _options.QualificationQualityWeight < 0 ||
            _options.PositionRepeatabilityWeight < 0 ||
            Math.Abs(
                _options.IncidentReliabilityWeight +
                _options.FinalQualificationWeight +
                _options.QualificationQualityWeight +
                _options.PositionRepeatabilityWeight - 1.0) > 0.000001)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Consistency component weights must be non-negative and sum to 1.");
        }

        if (_options.IncidentRateExponent <= 0 ||
            _options.IncidentToleranceRuns < 0 ||
            _options.RepeatedIncidentPenalty < 0 ||
            _options.RepeatedIncidentPenaltyExponent <= 0 ||
            _options.Q2QualificationScore is < 0 or > 100 ||
            _options.FinalIncidentReliabilityShare is < 0 or > 1 ||
            _options.PositionVariationAtHalfScore <= 0 ||
            _options.RobustPositionDeviationWeight is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Consistency reliability calibration is outside its valid range.");
        }

        if (_options.OutlierReferenceQuantile is <= 0 or > 1 ||
            _options.MinimumOutlierReferencePercentile is < 0 or > 1 ||
            _options.OutlierPercentileDropThreshold is <= 0 or > 1)
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

        var referencePercentile = Quantile(
            candidates.Select(sector => sector.Percentile!.Value),
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

        var cleanPercentiles = candidates
            .Where(sector => !outlierIndexes.Contains(sector.SectorIndex))
            .Select(sector => sector.Percentile!.Value)
            .ToArray();
        return new ConsistencyOutlierProfile(
            outlierIndexes,
            cleanPercentiles.Length == 0
                ? null
                : 100.0 * Quantile(cleanPercentiles, 0.50));
    }

    public RiderConsistencyResult Calculate(
        IReadOnlyList<ConsistencyRunInput> runs,
        CompetitionClassWeighting? competitionClassWeighting = null)
    {
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

            foreach (var sectorKey in expectedSectorKeys)
            {
                var attempts = courseRuns
                    .Select(run =>
                    {
                        var sector = run.Sectors.FirstOrDefault(candidate =>
                            string.Equals(
                                candidate.SectorKey,
                                sectorKey,
                                StringComparison.OrdinalIgnoreCase));
                        return new SectorAttempt(
                            run.RunId,
                            _sessionWeights.GetWeight(run.SessionType),
                            sector?.TimeSeconds,
                            sector?.IsOutlier == true);
                    })
                    .ToArray();
                var weightedTimes = attempts
                    .Where(attempt =>
                        attempt.TimeSeconds.HasValue &&
                        IsValidTime(attempt.TimeSeconds.Value))
                    .Select(attempt => new WeightedTime(
                        attempt.TimeSeconds!.Value,
                        attempt.Weight))
                    .ToArray();
                var totalAttemptWeight = attempts.Sum(attempt =>
                    attempt.Weight);
                var completedWeight = attempts
                    .Where(attempt =>
                        attempt.TimeSeconds.HasValue &&
                        IsValidTime(attempt.TimeSeconds.Value))
                    .Sum(attempt => attempt.Weight);
                var completionRate = totalAttemptWeight > 0
                    ? completedWeight / totalAttemptWeight
                    : 0;
                double? relativeVariation = null;
                var timeConsistency = 0.0;

                if (weightedTimes.Length >= 2)
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
                    attempts.Length,
                    weightedTimes.Length));

                totalComparisonOpportunities +=
                    attempts.Length * (attempts.Length - 1) / 2;
            }
        }

        var eventGroups = officialRuns
            .GroupBy(run => run.CourseKey, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Any(run => !IsIgnoredSportingStatus(run.Status)))
            .Select(group => group.ToArray())
            .ToArray();
        var incidentReliability = CalculateIncidentReliability(relevantRuns);
        var finalQualification = CalculateFinalQualification(eventGroups);
        var qualificationQuality = CalculateQualificationQuality(eventGroups);
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
        var repeatedIncidentPenalty = incidentCount <= 1
            ? 0
            : _options.RepeatedIncidentPenalty * Math.Pow(
                incidentCount - 1,
                _options.RepeatedIncidentPenaltyExponent);
        return Clamp(baseReliability - repeatedIncidentPenalty, 0, 100);
    }

    private static bool IsIncidentRun(ConsistencyRunInput run) =>
        string.Equals(run.Status, "DNF", StringComparison.OrdinalIgnoreCase) ||
        run.HasIncident;

    private static double CalculateFinalQualification(
        IReadOnlyCollection<ConsistencyRunInput[]> events)
    {
        if (events.Count == 0)
        {
            return 0;
        }

        var qualified = events.Count(runs =>
            runs.Any(run => run.SessionType == PaceSessionType.Final));
        return 100.0 * qualified / events.Count;
    }

    private double CalculateQualificationQuality(
        IReadOnlyCollection<ConsistencyRunInput[]> events)
    {
        if (events.Count == 0)
        {
            return 0;
        }

        return events.Average(runs =>
        {
            if (!runs.Any(run => run.SessionType == PaceSessionType.Final))
            {
                return 0;
            }

            return runs.Any(run =>
                run.SessionType == PaceSessionType.Qualifying2 &&
                !IsIgnoredSportingStatus(run.Status))
                    ? _options.Q2QualificationScore
                    : 100.0;
        });
    }

    private double CalculatePositionRepeatability(
        IReadOnlyCollection<ConsistencyRunInput> runs)
    {
        var positions = runs
            .Where(run =>
                run.IsClassified &&
                run.FinishRank is > 0 &&
                run.FieldSize is > 0 &&
                run.FinishRank <= run.FieldSize)
            .Select(run => new WeightedPosition(
                run.FieldSize == 1
                    ? 0
                    : (run.FinishRank!.Value - 1.0) / (run.FieldSize!.Value - 1.0),
                _sessionWeights.GetWeight(run.SessionType)))
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

    private static double WeightedMedian(
        IReadOnlyCollection<WeightedPosition> values)
    {
        var ordered = values.OrderBy(value => value.Value).ToArray();
        var halfWeight = ordered.Sum(value => value.Weight) / 2.0;
        var accumulated = 0.0;
        foreach (var value in ordered)
        {
            accumulated += value.Weight;
            if (accumulated >= halfWeight)
            {
                return value.Value;
            }
        }

        return ordered[^1].Value;
    }

    private static bool IsIgnoredSportingStatus(string? status) =>
        string.Equals(status, "DNS", StringComparison.OrdinalIgnoreCase) ||
        DownhillPaceCalculator.IsDisqualified(status);

    private static double CalculateWeightedRelativeVariation(
        IReadOnlyCollection<WeightedTime> times)
    {
        var validTimes = times
            .Where(item =>
                IsValidTime(item.TimeSeconds) &&
                item.Weight > 0 &&
                !double.IsNaN(item.Weight) &&
                !double.IsInfinity(item.Weight))
            .ToArray();

        if (validTimes.Length < 2)
        {
            return 0;
        }

        var weightSum = validTimes.Sum(item => item.Weight);
        if (weightSum <= 0)
        {
            return 0;
        }

        var weightedLogMean = validTimes.Sum(item =>
            Math.Log(item.TimeSeconds) * item.Weight) / weightSum;
        var weightedVariance = validTimes.Sum(item =>
            {
                var difference = Math.Log(item.TimeSeconds) - weightedLogMean;
                return item.Weight * difference * difference;
            }) / weightSum;

        return Math.Sqrt(Math.Exp(weightedVariance) - 1.0);
    }

    private static double Quantile(
        IEnumerable<double> values,
        double quantile)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        if (ordered.Length == 0)
        {
            return 0;
        }

        var position = (ordered.Length - 1) * Clamp(quantile, 0, 1);
        var lowerIndex = (int)Math.Floor(position);
        var upperIndex = (int)Math.Ceiling(position);
        if (lowerIndex == upperIndex)
        {
            return ordered[lowerIndex];
        }

        var fraction = position - lowerIndex;
        return ordered[lowerIndex] +
            fraction * (ordered[upperIndex] - ordered[lowerIndex]);
    }

    private double VariationToScore(double relativeVariation)
    {
        var normalized = relativeVariation / _options.VariationAtHalfScore;
        return 100.0 / (1.0 + normalized * normalized);
    }

    private static bool IsValidTime(double time)
    {
        return time > 0 && !double.IsNaN(time) && !double.IsInfinity(time);
    }

    private static double Clamp(double value, double minimum, double maximum)
    {
        return Math.Min(maximum, Math.Max(minimum, value));
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

    private sealed record SectorAttempt(
        string RunId,
        double Weight,
        double? TimeSeconds,
        bool IsOutlier);

    private sealed record WeightedTime(
        double TimeSeconds,
        double Weight);

    private sealed record WeightedPosition(
        double Value,
        double Weight);

}
