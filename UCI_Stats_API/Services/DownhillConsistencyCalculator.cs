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
    string? Status = null);

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
    IReadOnlyList<SectorConsistencyResult> Sectors);

internal sealed record ConsistencyOutlierProfile(
    IReadOnlySet<int> SectorIndexes,
    double? ProjectedFinishPositionScore);

internal sealed class ConsistencyOptions
{
    public double VariationAtHalfScore { get; init; } = 0.06;

    public double ConfidenceComparisonScale { get; init; } = 20.0;

    // Final session results are the primary source of Consistency.
    public double FinishPositionWeight { get; init; } = 0.90;

    // Sector-time repeatability remains a small supporting signal.
    public double SectorConsistencyWeight { get; init; } = 0.10;

    public int MaximumProtectedOutlierSectors { get; init; } = 4;

    public double OneOutlierMaximumLoss { get; init; } = 3.0;

    public double TwoOutliersMaximumLoss { get; init; } = 8.0;

    public double ThreeOutliersMaximumLoss { get; init; } = 12.0;

    public double FourOutliersMaximumLoss { get; init; } = 20.0;

    public double OutlierReferenceQuantile { get; init; } = 0.75;

    public double MinimumOutlierReferencePercentile { get; init; } = 0.60;

    public double OutlierPercentileDropThreshold { get; init; } = 0.35;
}

internal sealed class DownhillConsistencyCalculator
{
    private readonly ConsistencyOptions _options;

    public DownhillConsistencyCalculator(ConsistencyOptions? options = null)
    {
        _options = options ?? new ConsistencyOptions();
        if (_options.VariationAtHalfScore <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "VariationAtHalfScore must be greater than zero.");
        }

        if (_options.FinishPositionWeight < 0 ||
            _options.SectorConsistencyWeight < 0 ||
            Math.Abs(
                _options.FinishPositionWeight +
                _options.SectorConsistencyWeight - 1.0) > 0.000001)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Consistency component weights must be non-negative and sum to 1.");
        }

        if (_options.MaximumProtectedOutlierSectors < 0 ||
            _options.OneOutlierMaximumLoss < 0 ||
            _options.TwoOutliersMaximumLoss < _options.OneOutlierMaximumLoss ||
            _options.ThreeOutliersMaximumLoss < _options.TwoOutliersMaximumLoss ||
            _options.FourOutliersMaximumLoss < _options.ThreeOutliersMaximumLoss)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Consistency outlier protection limits are outside their valid ranges.");
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
        var relevantRuns = runs
            .Where(run =>
                GetConsistencySessionWeight(run.SessionType) > 0 &&
                !string.Equals(run.Status, "DNS", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (relevantRuns.Length < 2)
        {
            return EmptyResult(relevantRuns.Length);
        }

        var sectorResults = new List<SectorConsistencyResult>();
        var protectedSectorScores = new List<double>();
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
                            GetConsistencySessionWeight(run.SessionType) *
                                (competitionClassWeighting?.GetWeight(
                                    run.CompetitionClassPriority) ?? 1.0),
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
                protectedSectorScores.Add(CalculateProtectedSectorScore(
                    attempts,
                    completionRate,
                    rawSectorScore));

                totalComparisonOpportunities +=
                    attempts.Length * (attempts.Length - 1) / 2;
            }
        }

        var sectorConsistency = sectorResults.Count == 0
            ? (double?)null
            : sectorResults.Average(sector => sector.Consistency);
        var protectedSectorConsistency = protectedSectorScores.Count == 0
            ? (double?)null
            : protectedSectorScores.Average();
        var finishPositionScore = CalculateFinishPositionScore(
            relevantRuns,
            competitionClassWeighting,
            useProjectedScore: false);
        var protectedFinishPositionScore = CalculateFinishPositionScore(
            relevantRuns,
            competitionClassWeighting,
            useProjectedScore: true);
        var rawConsistency = BlendConsistency(
            finishPositionScore,
            sectorConsistency);
        var protectedConsistency = BlendConsistency(
            protectedFinishPositionScore,
            protectedSectorConsistency);
        var outlierSectorCount = relevantRuns.Sum(run =>
            run.Sectors.Count(sector => sector.IsOutlier));
        var consistency = ApplyOutlierProtection(
            rawConsistency,
            protectedConsistency,
            outlierSectorCount);

        if (!consistency.HasValue)
        {
            return new RiderConsistencyResult(
                Consistency: null,
                Confidence: 0,
                UsedRuns: relevantRuns.Length,
                UsedSectors: 0,
                Sectors: []);
        }

        var confidence = 1.0 - Math.Exp(
            -totalComparisonOpportunities /
            _options.ConfidenceComparisonScale);

        return new RiderConsistencyResult(
            Consistency: Clamp(consistency.Value, 0, 100),
            Confidence: Clamp(confidence, 0, 1),
            UsedRuns: relevantRuns.Length,
            UsedSectors: sectorResults.Count,
            Sectors: sectorResults);
    }

    private double? CalculateFinishPositionScore(
        IReadOnlyCollection<ConsistencyRunInput> runs,
        CompetitionClassWeighting? competitionClassWeighting,
        bool useProjectedScore)
    {
        var scores = runs
            .Where(run => run.FieldSize is > 0)
            .Select(run => new WeightedFinishPosition(
                Score: useProjectedScore &&
                    run.Sectors.Any(sector => sector.IsOutlier) &&
                    run.ProjectedFinishPositionScore.HasValue
                        ? Clamp(run.ProjectedFinishPositionScore.Value, 0, 100)
                        : CalculateRunFinishPositionScore(run),
                Weight: GetConsistencySessionWeight(run.SessionType) *
                    (competitionClassWeighting?.GetWeight(
                        run.CompetitionClassPriority) ?? 1.0)))
            .Where(result => result.Weight > 0)
            .ToArray();

        if (scores.Length == 0)
        {
            return null;
        }

        var weightSum = scores.Sum(result => result.Weight);
        return scores.Sum(result => result.Score * result.Weight) / weightSum;
    }

    private double CalculateProtectedSectorScore(
        IReadOnlyCollection<SectorAttempt> attempts,
        double completionRate,
        double rawSectorScore)
    {
        var cleanTimes = attempts
            .Where(attempt =>
                !attempt.IsOutlier &&
                attempt.TimeSeconds.HasValue &&
                IsValidTime(attempt.TimeSeconds.Value))
            .Select(attempt => new WeightedTime(
                attempt.TimeSeconds!.Value,
                attempt.Weight))
            .ToArray();
        if (cleanTimes.Length == 0 ||
            !attempts.Any(attempt => attempt.IsOutlier))
        {
            return rawSectorScore;
        }

        var replacementTime = WeightedGeometricMean(cleanTimes);
        var protectedTimes = attempts
            .Where(attempt =>
                attempt.TimeSeconds.HasValue &&
                IsValidTime(attempt.TimeSeconds.Value))
            .Select(attempt => new WeightedTime(
                attempt.IsOutlier
                    ? replacementTime
                    : attempt.TimeSeconds!.Value,
                attempt.Weight))
            .ToArray();
        var relativeVariation = CalculateWeightedRelativeVariation(protectedTimes);
        return Clamp(
            VariationToScore(relativeVariation) * completionRate,
            0,
            100);
    }

    private double? ApplyOutlierProtection(
        double? rawConsistency,
        double? protectedConsistency,
        int outlierSectorCount)
    {
        if (!rawConsistency.HasValue ||
            !protectedConsistency.HasValue ||
            outlierSectorCount <= 0 ||
            outlierSectorCount > _options.MaximumProtectedOutlierSectors ||
            rawConsistency.Value >= protectedConsistency.Value)
        {
            return rawConsistency;
        }

        var maximumLoss = outlierSectorCount switch
        {
            1 => _options.OneOutlierMaximumLoss,
            2 => _options.TwoOutliersMaximumLoss,
            3 => _options.ThreeOutliersMaximumLoss,
            _ => _options.FourOutliersMaximumLoss
        };
        return Math.Max(
            rawConsistency.Value,
            protectedConsistency.Value - maximumLoss);
    }

    private static double CalculateRunFinishPositionScore(
        ConsistencyRunInput run)
    {
        if (!run.IsClassified ||
            run.FinishRank is not { } rank ||
            run.FieldSize is not { } fieldSize ||
            rank < 1 ||
            rank > fieldSize)
        {
            return 0;
        }

        if (fieldSize == 1)
        {
            return rank == 1 ? 100 : 0;
        }

        return 100.0 * (fieldSize - rank) / (fieldSize - 1);
    }

    private double? BlendConsistency(
        double? finishPositionScore,
        double? sectorConsistency)
    {
        if (finishPositionScore.HasValue && sectorConsistency.HasValue)
        {
            return
                _options.FinishPositionWeight * finishPositionScore.Value +
                _options.SectorConsistencyWeight * sectorConsistency.Value;
        }

        return finishPositionScore ?? sectorConsistency;
    }

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

    private static double WeightedGeometricMean(
        IReadOnlyCollection<WeightedTime> times)
    {
        var weightSum = times.Sum(item => item.Weight);
        return weightSum <= 0
            ? times.Average(item => item.TimeSeconds)
            : Math.Exp(times.Sum(item =>
                Math.Log(item.TimeSeconds) * item.Weight) / weightSum);
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

    private sealed record WeightedFinishPosition(
        double Score,
        double Weight);
}
