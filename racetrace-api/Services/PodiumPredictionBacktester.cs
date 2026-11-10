namespace RaceTrace.Api.Services;

internal sealed record PodiumBacktestPrediction(
    string EventId,
    string RiderId,
    int ActualPosition,
    double PodiumProbability,
    double RankingScore,
    double? WinProbability = null
);

internal sealed record PodiumBacktestMetrics(
    double WinnerAccuracy,
    double PodiumRecallAt3,
    double PodiumRecallAt5,
    double BrierScore,
    double LogLoss,
    double NdcgAt3
);

internal static class PodiumPredictionBacktester
{
    public static PodiumBacktestMetrics Evaluate(
        IReadOnlyList<PodiumBacktestPrediction> predictions
    )
    {
        var events = predictions
            .GroupBy(value => value.EventId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Any())
            .ToArray();
        if (events.Length == 0)
        {
            return new PodiumBacktestMetrics(0, 0, 0, 0, 0, 0);
        }

        var winnerHits = 0;
        var recall3 = 0.0;
        var recall5 = 0.0;
        var brier = 0.0;
        var logLoss = 0.0;
        var ndcg = 0.0;
        var observations = 0;

        foreach (var eventGroup in events)
        {
            var ordered = eventGroup.OrderByDescending(value => value.RankingScore).ToArray();
            if (
                eventGroup
                    .OrderByDescending(value => value.WinProbability ?? value.RankingScore)
                    .First()
                    .ActualPosition == 1
            )
            {
                winnerHits++;
            }

            var podium = eventGroup.Where(value => IsPodium(value.ActualPosition)).ToArray();
            var denominator = Math.Max(1, podium.Length);
            recall3 +=
                ordered.Take(3).Count(value => IsPodium(value.ActualPosition))
                / (double)denominator;
            recall5 +=
                ordered.Take(5).Count(value => IsPodium(value.ActualPosition))
                / (double)denominator;
            ndcg += NdcgAt3(ordered);

            foreach (var prediction in eventGroup)
            {
                var actual = IsPodium(prediction.ActualPosition) ? 1.0 : 0.0;
                var probability = Math.Clamp(prediction.PodiumProbability, 0.000001, 0.999999);
                brier += Math.Pow(probability - actual, 2);
                logLoss -=
                    actual * Math.Log(probability) + (1 - actual) * Math.Log(1 - probability);
                observations++;
            }
        }

        return new PodiumBacktestMetrics(
            winnerHits / (double)events.Length,
            recall3 / events.Length,
            recall5 / events.Length,
            brier / Math.Max(1, observations),
            logLoss / Math.Max(1, observations),
            ndcg / events.Length
        );
    }

    private static double NdcgAt3(IReadOnlyList<PodiumBacktestPrediction> ordered)
    {
        var dcg = ordered
            .Take(3)
            .Select(
                (value, index) =>
                {
                    var relevance = IsPodium(value.ActualPosition) ? 4 - value.ActualPosition : 0;
                    return (Math.Pow(2, relevance) - 1) / Math.Log2(index + 2);
                }
            )
            .Sum();
        var ideal = new[] { 3, 2, 1 }
            .Select((relevance, index) => (Math.Pow(2, relevance) - 1) / Math.Log2(index + 2))
            .Sum();
        return ideal <= 0 ? 0 : dcg / ideal;
    }

    private static bool IsPodium(int position) => position is > 0 and <= 3;
}
