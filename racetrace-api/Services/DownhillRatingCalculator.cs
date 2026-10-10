using RaceTrace.Api.Infrastructure.Calculations;

namespace RaceTrace.Api.Services;

internal sealed record RatingResult(
    double Rating,
    double RawRating,
    double Pace,
    double Consistency,
    double? ResultScore);


internal sealed class DownhillRatingCalculator
{
    private readonly RatingOptions _options;

    public DownhillRatingCalculator(RatingOptions? options = null)
    {
        _options = options ?? AlgorithmConfiguration.Rating;
        if (_options.PaceWeight < 0 ||
            _options.ConsistencyWeight < 0 ||
            _options.ResultScoreWeight < 0 ||
            Math.Abs(
                _options.PaceWeight +
                _options.ConsistencyWeight +
                _options.ResultScoreWeight - 1.0) > 0.000001)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Rating component weights must be non-negative and sum to 1.");
        }
    }

    public RatingResult Calculate(
        double pace,
        double consistency)
    {
        return Calculate(
            pace,
            consistency,
            resultScore: null);
    }

    public RatingResult Calculate(
        double pace,
        double consistency,
        double? resultScore)
    {
        pace = Clamp(pace, 0, 100);
        consistency = Clamp(consistency, 0, 100);
        resultScore = resultScore.HasValue
            ? Clamp(resultScore.Value, 0, 100)
            : null;
        var activeResultWeight = resultScore.HasValue
            ? _options.ResultScoreWeight
            : 0;
        var activeWeightSum =
            _options.PaceWeight +
            _options.ConsistencyWeight +
            activeResultWeight;

        var rawRating = Clamp((
            _options.PaceWeight * pace +
            _options.ConsistencyWeight * consistency +
            activeResultWeight * (resultScore ?? 0)) / activeWeightSum,
            0,
            100);
        var contrastedRating = ApplyRatingContrast(rawRating);

        return new RatingResult(
            Rating: contrastedRating,
            RawRating: rawRating,
            Pace: pace,
            Consistency: consistency,
            ResultScore: resultScore);
    }

    internal double ApplyRatingContrast(double rawRating)
    {
        rawRating = Clamp(rawRating, 0, 100);
        if (_options.RatingContrast <= 0.000001)
        {
            return rawRating;
        }

        var normalized = (rawRating - 50.0) / 50.0;
        var denominator = Math.Tanh(_options.RatingContrast);
        if (Math.Abs(denominator) < 0.000001)
        {
            return rawRating;
        }

        var contrasted = Math.Tanh(
            _options.RatingContrast * normalized) / denominator;
        return Clamp(50.0 + 50.0 * contrasted, 0, 100);
    }

    private static double Clamp(double value, double minimum, double maximum)
    {
        return Math.Min(maximum, Math.Max(minimum, value));
    }
}
