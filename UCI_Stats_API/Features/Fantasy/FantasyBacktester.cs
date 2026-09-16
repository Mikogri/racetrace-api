namespace UCI_Stats_API.Features.Fantasy;

public sealed record FantasyBacktestRider(
    string PinkbikeExternalId,
    string? RiderId,
    string Gender,
    decimal ExpectedPoints,
    decimal ActualPoints);

public sealed record FantasyHistoricalPrice(
    string PinkbikeExternalId,
    string RoundId,
    decimal Price,
    DateTimeOffset FetchedAtUtc);

public sealed record FantasyBacktestMetrics(
    int RiderCount,
    double MeanAbsoluteError,
    double RootMeanSquaredError,
    double? SpearmanRankCorrelation,
    double TopKRecall,
    decimal? PredictedTeamActualPoints,
    decimal? OracleActualPoints,
    decimal? Regret,
    string? SalaryOptimizationUnavailableReason);

/// <summary>Evaluation only. Historical forecasts must be generated before the target round starts.</summary>
public static class FantasyBacktester
{
    public static FantasyBacktestMetrics Evaluate(
        IReadOnlyList<FantasyBacktestRider> riders,
        FantasyRules rules,
        string roundId,
        DateTimeOffset roundStartsAtUtc,
        DateTimeOffset trainingDataThroughUtc,
        IReadOnlyList<FantasyHistoricalPrice> historicalPrices,
        int topK = 6,
        CancellationToken cancellationToken = default)
    {
        if (trainingDataThroughUtc >= roundStartsAtUtc)
            throw new ArgumentException("Training data must precede the entire predicted round.", nameof(trainingDataThroughUtc));
        if (topK <= 0 || riders.Count == 0)
            throw new ArgumentException("Non-empty observations and a positive top K are required.", nameof(riders));
        if (riders.Any(x => x.ExpectedPoints < 0 || x.ActualPoints < 0 || string.IsNullOrWhiteSpace(x.PinkbikeExternalId))
            || riders.Select(x => x.PinkbikeExternalId).Distinct(StringComparer.Ordinal).Count() != riders.Count)
            throw new ArgumentException("Backtest rider identities must be unique and points non-negative.", nameof(riders));

        var errors = riders.Select(x => (double)(x.ExpectedPoints - x.ActualPoints)).ToArray();
        var predictedRanks = AverageRanks(riders.Select(x => x.ExpectedPoints).ToArray());
        var actualRanks = AverageRanks(riders.Select(x => x.ActualPoints).ToArray());
        var k = Math.Min(topK, riders.Count);
        var actualTop = riders.OrderByDescending(x => x.ActualPoints).ThenBy(x => x.PinkbikeExternalId, StringComparer.Ordinal)
            .Take(k).Select(x => x.PinkbikeExternalId).ToHashSet(StringComparer.Ordinal);
        var recall = riders.OrderByDescending(x => x.ExpectedPoints).ThenBy(x => x.PinkbikeExternalId, StringComparer.Ordinal)
            .Take(k).Count(x => actualTop.Contains(x.PinkbikeExternalId)) / (double)k;

        // Only a real snapshot associated with this round and known before it is eligible.
        // A current price, or an unrelated previous-round price, is never a fallback.
        var eligiblePrices = historicalPrices.Where(x => x.RoundId == roundId && x.FetchedAtUtc < roundStartsAtUtc)
            .GroupBy(x => x.FetchedAtUtc)
            .OrderByDescending(x => x.Key)
            .Select(snapshot => snapshot.ToArray())
            .FirstOrDefault(snapshot => snapshot.Select(x => x.PinkbikeExternalId).Distinct(StringComparer.Ordinal).Count() == snapshot.Length
                && riders.All(rider => snapshot.Any(price => price.PinkbikeExternalId == rider.PinkbikeExternalId && price.Price > 0)))
            ?.ToDictionary(x => x.PinkbikeExternalId, StringComparer.Ordinal);
        string? unavailable = null;
        decimal? selectedActual = null;
        decimal? oracleActual = null;
        if (eligiblePrices is null)
            unavailable = "Historical prices for the target round have no complete contemporaneous snapshot; projection-only evaluation.";
        else
        {
            var candidates = riders.Select(x => new FantasyCandidate(x.PinkbikeExternalId, x.RiderId, x.Gender,
                eligiblePrices[x.PinkbikeExternalId].Price, x.ExpectedPoints)).ToArray();
            var predicted = FantasyOptimizer.Optimize(candidates, rules, cancellationToken);
            var actualById = riders.ToDictionary(x => x.PinkbikeExternalId, x => x.ActualPoints, StringComparer.Ordinal);
            var oracle = FantasyOptimizer.Optimize(candidates.Select(x => x with
                { ExpectedFantasyPoints = actualById[x.PinkbikeExternalId] }).ToArray(), rules, cancellationToken);
            if (predicted is null || oracle is null)
                unavailable = "No legal historical lineup exists for this candidate pool.";
            else
            {
                selectedActual = predicted.Riders.Sum(x => actualById[x.PinkbikeExternalId]);
                oracleActual = oracle.ExpectedFantasyPoints;
            }
        }
        return new(riders.Count, errors.Average(Math.Abs), Math.Sqrt(errors.Average(x => x * x)),
            Correlation(predictedRanks, actualRanks), recall, selectedActual, oracleActual,
            oracleActual - selectedActual, unavailable);
    }

    private static double[] AverageRanks(decimal[] values)
    {
        var sorted = values.Select((value, index) => (value, index)).OrderBy(x => x.value).ToArray();
        var ranks = new double[values.Length];
        for (var first = 0; first < sorted.Length;)
        {
            var end = first + 1;
            while (end < sorted.Length && sorted[end].value == sorted[first].value) end++;
            var averageRank = (first + 1 + end) / 2d;
            for (var i = first; i < end; i++) ranks[sorted[i].index] = averageRank;
            first = end;
        }
        return ranks;
    }

    private static double? Correlation(double[] left, double[] right)
    {
        var leftMean = left.Average();
        var rightMean = right.Average();
        var covariance = 0d;
        var leftVariance = 0d;
        var rightVariance = 0d;
        for (var i = 0; i < left.Length; i++)
        {
            var x = left[i] - leftMean;
            var y = right[i] - rightMean;
            covariance += x * y;
            leftVariance += x * x;
            rightVariance += y * y;
        }
        return leftVariance == 0 || rightVariance == 0 ? null : covariance / Math.Sqrt(leftVariance * rightVariance);
    }
}
