namespace RaceTrace.Api.Services;

internal sealed partial class DownhillConsistencyCalculator
{
    private static double WeightedMedian(IReadOnlyCollection<WeightedPosition> values)
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

    private static double CalculateWeightedRelativeVariation(
        IReadOnlyCollection<WeightedTime> times
    )
    {
        var validTimes = times
            .Where(item =>
                IsValidTime(item.TimeSeconds)
                && item.Weight > 0
                && !double.IsNaN(item.Weight)
                && !double.IsInfinity(item.Weight)
            )
            .Select(item => (LogTime: Math.Log(item.TimeSeconds), item.Weight))
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

        var weightedLogMean = validTimes.Sum(item => item.LogTime * item.Weight) / weightSum;
        var weightedVariance =
            validTimes.Sum(item =>
            {
                var difference = item.LogTime - weightedLogMean;
                return item.Weight * difference * difference;
            }) / weightSum;

        return Math.Sqrt(Math.Exp(weightedVariance) - 1.0);
    }

    private static double QuantileSorted(double[] ordered, double quantile)
    {
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
        return ordered[lowerIndex] + fraction * (ordered[upperIndex] - ordered[lowerIndex]);
    }

    private static double Clamp(double value, double minimum, double maximum)
    {
        return Math.Min(maximum, Math.Max(minimum, value));
    }

    private static bool IsValidTime(double time)
    {
        return time > 0 && !double.IsNaN(time) && !double.IsInfinity(time);
    }

    private readonly record struct WeightedTime(double TimeSeconds, double Weight);

    private readonly record struct WeightedPosition(double Value, double Weight);
}
