namespace RaceTrace.Api.Services;

internal sealed partial class DownhillPaceCalculator
{
    private static FieldStatistics BuildFieldStatistics(IReadOnlyCollection<double> input)
    {
        var values = input
            .Where(value => !double.IsNaN(value) && !double.IsInfinity(value) && value > 0)
            .OrderBy(value => value)
            .ToArray();

        if (values.Length == 0)
        {
            return new FieldStatistics([], 0, 0, 0, 1, 1, null, null);
        }

        var median = MedianSorted(values);
        var logs = values.Select(value => Math.Log(value)).ToArray();
        var logMedian = MedianSorted(logs);
        var logMad = Median(logs.Select(value => Math.Abs(value - logMedian)));
        var linearMad = Median(values.Select(value => Math.Abs(value - median)));
        var logMadScale = Math.Max(1.4826 * logMad, 0.003);
        var linearMadScale = Math.Max(1.4826 * linearMad, Math.Max(median * 0.003, 0.01));

        return new FieldStatistics(
            values,
            values.Length,
            median,
            logMedian,
            logMadScale,
            linearMadScale,
            values[0],
            values.Length > 1 ? values[1] : null
        );
    }

    private static FieldStatistics ToFieldStatistics(PaceFieldSummary summary)
    {
        return new FieldStatistics(
            [],
            summary.Count,
            summary.Median,
            summary.LogMedian,
            summary.LogMadScale,
            summary.LinearMadScale,
            summary.BestValue,
            summary.SecondBestValue
        );
    }

    private static PaceFieldSummary ToFieldSummary(FieldStatistics statistics)
    {
        return new PaceFieldSummary(
            statistics.Count,
            statistics.Median,
            statistics.LogMedian,
            statistics.LogMadScale,
            statistics.LinearMadScale,
            statistics.BestValue,
            statistics.SecondBestValue
        );
    }

    private static int? CalculateRank(
        double? value,
        FieldStatistics statistics,
        bool lowerIsBetter = true
    )
    {
        if (!value.HasValue || !double.IsFinite(value.Value) || value.Value <= 0)
        {
            return null;
        }

        // Lower bound for times, upper bound for speeds: equal values share rank.
        var values = statistics.Values;
        var low = 0;
        var high = values.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (lowerIsBetter ? values[middle] < value.Value : values[middle] <= value.Value)
                low = middle + 1;
            else
                high = middle;
        }

        return lowerIsBetter ? low + 1 : values.Length - low + 1;
    }

    private static double Median(IEnumerable<double> input)
    {
        return MedianSorted(input.OrderBy(value => value).ToArray());
    }

    private static double MedianSorted(double[] values)
    {
        if (values.Length == 0)
        {
            return 0;
        }

        var middle = values.Length / 2;
        return values.Length % 2 == 1
            ? values[middle]
            : (values[middle - 1] + values[middle]) / 2.0;
    }

    private static double Sigmoid(double value)
    {
        if (value >= 0)
        {
            var exp = Math.Exp(-value);
            return 1.0 / (1.0 + exp);
        }

        var negativeExp = Math.Exp(value);
        return negativeExp / (1.0 + negativeExp);
    }

    private static double Clamp(double value, double minimum, double maximum)
    {
        return Math.Min(maximum, Math.Max(minimum, value));
    }

    private sealed record FieldStatistics(
        double[] Values,
        int Count,
        double Median,
        double LogMedian,
        double LogMadScale,
        double LinearMadScale,
        double? BestValue,
        double? SecondBestValue
    );
}
