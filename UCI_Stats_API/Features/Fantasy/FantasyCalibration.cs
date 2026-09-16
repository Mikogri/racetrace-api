namespace UCI_Stats_API.Features.Fantasy;

public sealed record FantasyCalibrationObservation(string EventId, DateTimeOffset CompletedAtUtc, IReadOnlyDictionary<int, decimal> RawProbabilities, int Outcome);
public sealed record FantasyStageCalibration(decimal Blend, IReadOnlyDictionary<int, decimal> EmpiricalProbabilities, int Samples, int Events, DateTimeOffset TrainedThroughUtc);

/// <summary>One fitted parameter per category/stage, applied to the existing full Monte Carlo distribution.</summary>
public static class FantasyCalibration
{
    public const string Version = "fantasy-empirical-blend-v3";

    public static FantasyStageCalibration? Fit(IReadOnlyList<FantasyCalibrationObservation> history, DateTimeOffset forecastAtUtc)
    {
        foreach (var observation in history)
        {
            ValidateRaw(observation.RawProbabilities);
            if (observation.Outcome < 0 || string.IsNullOrWhiteSpace(observation.EventId))
                throw new ArgumentException("Calibration outcomes and event identifiers must be valid.", nameof(history));
        }
        if (history.Any(value => value.CompletedAtUtc >= forecastAtUtc))
            throw new ArgumentException("Calibration cannot include the predicted round or later data.", nameof(history));
        if (history.Select(value => value.EventId).Distinct().Count() < 2) return null;
        // Outcome zero is explicitly non-scoring, not a fabricated last-place finish.
        var prior = history.GroupBy(value => value.Outcome).ToDictionary(group => group.Key, group => group.Count() / (decimal)history.Count);
        var nonScoring = prior.GetValueOrDefault(0);
        var baselines = history.Select(value => value.RawProbabilities.Keys.Max()).Distinct()
            .ToDictionary(fieldSize => fieldSize, fieldSize => ExchangeableBaseline(fieldSize, nonScoring));
        var likelihoods = history.Select(value => (Raw: value.RawProbabilities.GetValueOrDefault(value.Outcome),
            Baseline: value.Outcome == 0 ? nonScoring : baselines[value.RawProbabilities.Keys.Max()].GetValueOrDefault(value.Outcome))).ToArray();
        decimal bestBlend = 0;
        var bestLoss = double.PositiveInfinity;
        for (var step = 0; step <= 100; step++)
        {
            var blend = step / 100m;
            var loss = likelihoods.Sum(value => -Math.Log(Math.Max(1e-12,
                (double)((1 - blend) * value.Raw + blend * value.Baseline))));
            if (loss < bestLoss) { bestLoss = loss; bestBlend = blend; }
        }
        return new(bestBlend, prior, history.Count, history.Select(value => value.EventId).Distinct().Count(), history.Max(value => value.CompletedAtUtc));
    }

    public static FantasyPositionDistribution Apply(IReadOnlyDictionary<int, decimal> raw, FantasyStageCalibration calibration)
    {
        ValidateRaw(raw);
        if (calibration.Blend is < 0 or > 1 || calibration.EmpiricalProbabilities.Any(pair => pair.Key < 0 || pair.Value < 0) ||
            Math.Abs(calibration.EmpiricalProbabilities.Values.Sum() - 1) > 0.000000001m)
            throw new ArgumentException("Invalid fitted calibration.", nameof(calibration));
        var nonScoring = calibration.EmpiricalProbabilities.GetValueOrDefault(0);
        var baseline = ExchangeableBaseline(raw.Keys.Max(), nonScoring);
        var probabilities = baseline.ToDictionary(pair => pair.Key,
            pair => (1 - calibration.Blend) * raw.GetValueOrDefault(pair.Key) + calibration.Blend * pair.Value);
        return new(probabilities, calibration.Blend * nonScoring);
    }

    private static IReadOnlyDictionary<int, decimal> ExchangeableBaseline(int fieldSize, decimal nonScoring)
    {
        // A coherent joint baseline: each rider independently finishes with probability 1-q,
        // then the finishers receive a uniformly random order. Given K finishers, at most
        // one rider occupies place k; exchangeability gives P(rider at k) = P(K >= k)/N.
        // Thus every row sums to 1-q and every position's total occupancy is <= 1.
        // Dynamic programming avoids a binomial recurrence that underflows in the tails.
        var finishCounts = new decimal[fieldSize + 1];
        finishCounts[0] = 1;
        for (var riders = 1; riders <= fieldSize; riders++)
        {
            for (var finishers = riders; finishers >= 1; finishers--)
                finishCounts[finishers] = finishCounts[finishers] * nonScoring
                    + finishCounts[finishers - 1] * (1 - nonScoring);
            finishCounts[0] *= nonScoring;
        }
        var probabilities = new Dictionary<int, decimal>(fieldSize);
        var tail = 0m;
        for (var position = fieldSize; position >= 1; position--)
        {
            tail += finishCounts[position];
            probabilities[position] = tail / fieldSize;
        }
        return probabilities;
    }

    private static void ValidateRaw(IReadOnlyDictionary<int, decimal> raw)
    {
        if (raw.Count == 0 || raw.Any(pair => pair.Key <= 0 || pair.Value < 0) || Math.Abs(raw.Values.Sum() - 1) > 0.000000001m)
            throw new ArgumentException("A complete existing race probability distribution is required.", nameof(raw));
    }
}
