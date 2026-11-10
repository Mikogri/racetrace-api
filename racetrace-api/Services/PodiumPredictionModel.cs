using RaceTrace.Api.Infrastructure.Telemetry;

namespace RaceTrace.Api.Services;

internal sealed record PodiumPredictionFeatures(
    string RiderId,
    decimal? CurrentRating,
    decimal? TrackPace,
    decimal? PreviousRating,
    double Rating,
    double Pace,
    double Confidence,
    double Consistency,
    double RecentForm,
    double TrackAffinity,
    double ConditionsAffinity,
    double WeekendPerformance,
    double WchPerformanceDelta,
    double TitleLeverage,
    double PressureResponse,
    double IncidentProbability,
    int EvidenceCount
);

internal sealed record PodiumPredictionDistribution(
    PodiumPredictionFeatures Features,
    double ExpectedPerformance,
    double PerformanceUncertainty,
    double IncidentProbability
);

internal sealed record MonteCarloPrediction(
    double WinProbability,
    double PodiumProbability,
    double Top5Probability,
    double ExpectedPosition
);

internal sealed record MonteCarloRaceResult(
    MonteCarloPrediction Summary,
    IReadOnlyList<double> PositionProbabilities
);

internal sealed class PodiumPredictionCalibration
{
    private readonly PodiumPredictionOptions _options;

    public PodiumPredictionCalibration(PodiumPredictionOptions options)
    {
        _options = options;
    }

    public IReadOnlyList<PodiumPredictionDistribution> Calculate(
        IReadOnlyList<PodiumPredictionFeatures> features,
        bool isWorldChampionship
    )
    {
        if (features.Count == 0)
        {
            return [];
        }

        var rawSkills = features.Select(RawSkill).ToArray();
        var fieldMean = rawSkills.Average();
        return features
            .Select(
                (feature, index) =>
                {
                    var confidence = Math.Clamp(feature.Confidence / 100.0, 0, 1);
                    var expected = fieldMean + confidence * (rawSkills[index] - fieldMean);
                    var consistency = Math.Clamp(feature.Consistency / 100.0, 0, 1);
                    var uncertainty =
                        _options.MinimumUncertainty
                        + (1 - consistency) * _options.ConsistencyUncertaintyRange
                        + (1 - confidence) * _options.ConfidenceUncertaintyRange
                        + _options.EvidenceUncertaintyScale
                            / Math.Sqrt(Math.Max(1, feature.EvidenceCount));
                    if (isWorldChampionship)
                    {
                        uncertainty *= _options.WchUncertaintyMultiplier;
                    }

                    return new PodiumPredictionDistribution(
                        feature,
                        Math.Clamp(expected, 0, 100),
                        Math.Clamp(
                            uncertainty,
                            _options.MinimumUncertainty,
                            _options.MaximumUncertainty
                        ),
                        Math.Clamp(
                            feature.IncidentProbability,
                            0,
                            _options.MaximumIncidentProbability
                        )
                    );
                }
            )
            .ToArray();
    }

    private double RawSkill(PodiumPredictionFeatures feature)
    {
        var baseWeight = _options.RatingCoefficient + _options.PaceCoefficient;
        var basePerformance =
            baseWeight <= 0
                ? 50
                : (
                    _options.RatingCoefficient * feature.Rating
                    + _options.PaceCoefficient * feature.Pace
                ) / baseWeight;
        return basePerformance
            + _options.RecentFormCoefficient * feature.RecentForm
            + _options.TrackAffinityCoefficient * feature.TrackAffinity
            + _options.ConditionsAffinityCoefficient * feature.ConditionsAffinity
            + _options.WeekendPerformanceCoefficient * feature.WeekendPerformance
            + _options.WchDeltaCoefficient * feature.WchPerformanceDelta
            + feature.PressureResponse * feature.TitleLeverage;
    }
}

internal sealed class MonteCarloRaceSimulator
{
    private readonly PodiumPredictionOptions _options;

    public MonteCarloRaceSimulator(PodiumPredictionOptions options)
    {
        _options = options;
    }

    public IReadOnlyList<MonteCarloPrediction> Simulate(
        IReadOnlyList<PodiumPredictionDistribution> riders
    ) => SimulateDetailed(riders).Select(result => result.Summary).ToArray();

    internal IReadOnlyList<MonteCarloRaceResult> SimulateDetailed(
        IReadOnlyList<PodiumPredictionDistribution> riders
    )
    {
        using var measurement = ApiTelemetry.Measure("podium");
        if (riders.Count == 0)
        {
            return [];
        }

        var wins = new int[riders.Count];
        var podiums = new int[riders.Count];
        var top5 = new int[riders.Count];
        var positionSums = new long[riders.Count];
        var positionCounts = Enumerable
            .Range(0, riders.Count)
            .Select(_ => new int[riders.Count])
            .ToArray();
        var random = new Random(_options.RandomSeed);
        var sampled = new (int Index, double Performance)[riders.Count];

        for (var simulation = 0; simulation < _options.MonteCarloIterations; simulation++)
        {
            for (var rider = 0; rider < riders.Count; rider++)
            {
                var distribution = riders[rider];
                var incident = random.NextDouble() < distribution.IncidentProbability;
                var performance =
                    distribution.ExpectedPerformance
                    + distribution.PerformanceUncertainty * StudentT(random);
                if (incident)
                {
                    performance -=
                        _options.IncidentPerformanceLoss
                        + _options.IncidentTailLoss * Math.Abs(StudentT(random));
                }

                sampled[rider] = (rider, performance);
            }

            Array.Sort(sampled, (left, right) => right.Performance.CompareTo(left.Performance));
            for (var position = 0; position < sampled.Length; position++)
            {
                var rider = sampled[position].Index;
                var officialPosition = position + 1;
                positionSums[rider] += officialPosition;
                positionCounts[rider][position]++;
                if (officialPosition == 1)
                    wins[rider]++;
                if (officialPosition <= 3)
                    podiums[rider]++;
                if (officialPosition <= 5)
                    top5[rider]++;
            }
        }

        return Enumerable
            .Range(0, riders.Count)
            .Select(index => new MonteCarloRaceResult(
                new MonteCarloPrediction(
                    wins[index] / (double)_options.MonteCarloIterations,
                    podiums[index] / (double)_options.MonteCarloIterations,
                    top5[index] / (double)_options.MonteCarloIterations,
                    positionSums[index] / (double)_options.MonteCarloIterations
                ),
                positionCounts[index]
                    .Select(count => count / (double)_options.MonteCarloIterations)
                    .ToArray()
            ))
            .ToArray();
    }

    private double StudentT(Random random)
    {
        var normal = StandardNormal(random);
        var chiSquare = 0.0;
        for (var i = 0; i < _options.StudentTDegreesOfFreedom; i++)
        {
            var sample = StandardNormal(random);
            chiSquare += sample * sample;
        }

        return normal
            / Math.Sqrt(Math.Max(chiSquare, double.Epsilon) / _options.StudentTDegreesOfFreedom);
    }

    private static double StandardNormal(Random random)
    {
        var u1 = Math.Max(random.NextDouble(), double.Epsilon);
        var u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
