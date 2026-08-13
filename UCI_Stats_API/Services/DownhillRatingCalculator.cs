namespace UCI_Stats_API.Services;

internal sealed record RatingResult(
    double Rating,
    double RawRating,
    double Pace,
    double Consistency,
    double? ResultScore);

internal sealed class RatingOptions
{
    public double PaceWeight { get; init; } = 0.70;
    public double ConsistencyWeight { get; init; } = 0.15;
    public double ResultScoreWeight { get; init; } = 0.15;
    public double RatingContrast { get; init; } = 1.20;
}

internal sealed class DownhillRatingCalculator
{
    private readonly RatingOptions _options;

    public DownhillRatingCalculator(RatingOptions? options = null)
    {
        _options = options ?? new RatingOptions();
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
        double consistency,
        double paceConfidence,
        double consistencyConfidence)
    {
        return Calculate(
            pace,
            consistency,
            resultScore: null,
            paceConfidence,
            consistencyConfidence,
            resultScoreConfidence: 0);
    }

    public RatingResult Calculate(
        double pace,
        double consistency,
        double? resultScore,
        double paceConfidence,
        double consistencyConfidence,
        double resultScoreConfidence)
    {
        pace = Clamp(pace, 0, 100);
        consistency = Clamp(consistency, 0, 100);
        resultScore = resultScore.HasValue
            ? Clamp(resultScore.Value, 0, 100)
            : null;
        paceConfidence = Clamp(paceConfidence, 0, 1);
        consistencyConfidence = Clamp(consistencyConfidence, 0, 1);
        resultScoreConfidence = Clamp(resultScoreConfidence, 0, 1);

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
        var ratingConfidence = Clamp((
            _options.PaceWeight * paceConfidence +
            _options.ConsistencyWeight * consistencyConfidence +
            activeResultWeight * resultScoreConfidence) / activeWeightSum,
            0,
            1);
        var rating = rawRating +
            (contrastedRating - rawRating) * ratingConfidence;

        return new RatingResult(
            Rating: Clamp(rating, 0, 100),
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
