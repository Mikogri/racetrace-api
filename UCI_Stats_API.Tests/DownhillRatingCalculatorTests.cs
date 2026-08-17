using UCI_Stats_API.Services;

namespace UCI_Stats_API.Tests;

public sealed class DownhillRatingCalculatorTests
{
    [Fact]
    public void DefaultOptionsUseSeventyPercentPaceWeight()
    {
        var options = new RatingOptions();

        Assert.Equal(0.70, options.PaceWeight, precision: 10);
        Assert.Equal(0.15, options.ConsistencyWeight, precision: 10);
        Assert.Equal(0.15, options.ResultScoreWeight, precision: 10);
        Assert.Equal(1.20, options.RatingContrast, precision: 10);
    }

    [Fact]
    public void RawRatingRenormalizesActivePaceAndConsistencyWeights()
    {
        var result = new DownhillRatingCalculator().Calculate(
            pace: 94,
            consistency: 80);

        var expectedRawRating = (0.70 * 94 + 0.15 * 80) / 0.85;

        Assert.Equal(expectedRawRating, result.RawRating, precision: 10);
        Assert.Equal(94, result.Pace, precision: 10);
        Assert.Equal(80, result.Consistency, precision: 10);
        Assert.Null(result.ResultScore);
    }

    [Fact]
    public void ResultScoreChangesRawRatingWhenPositionsAreAvailable()
    {
        var calculator = new DownhillRatingCalculator();
        var withoutResultScore = calculator.Calculate(90, 80);
        var winningResults = calculator.Calculate(
            pace: 90,
            consistency: 80,
            resultScore: 100);
        var weakResults = calculator.Calculate(
            pace: 90,
            consistency: 80,
            resultScore: 0);

        Assert.Equal((0.70 * 90 + 0.15 * 80) / 0.85, withoutResultScore.RawRating, precision: 10);
        Assert.Equal(90, winningResults.RawRating, precision: 10);
        Assert.Equal(75, weakResults.RawRating, precision: 10);
        Assert.True(winningResults.Rating > withoutResultScore.Rating);
        Assert.True(weakResults.Rating < withoutResultScore.Rating);
    }

    [Fact]
    public void RatingAlwaysUsesFullContrast()
    {
        var calculator = new DownhillRatingCalculator();
        var result = calculator.Calculate(
            pace: 90,
            consistency: 80,
            resultScore: 100);

        Assert.Equal(
            calculator.ApplyRatingContrast(result.RawRating),
            result.Rating,
            precision: 10);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(100)]
    public void ContrastKeepsFixedScalePoints(double rawRating)
    {
        var result = CalculateForRaw(rawRating);

        Assert.Equal(rawRating, result.Rating, precision: 10);
    }

    [Fact]
    public void RatingsAboveFiftyAreIncreased()
    {
        var result = CalculateForRaw(70);

        Assert.True(result.Rating > result.RawRating);
    }

    [Fact]
    public void RatingsBelowFiftyAreDecreased()
    {
        var result = CalculateForRaw(30);

        Assert.True(result.Rating < result.RawRating);
    }

    [Fact]
    public void ContrastIsStrictlyMonotonic()
    {
        var ratings = Enumerable.Range(0, 101)
            .Select(rawRating => CalculateForRaw(rawRating).Rating)
            .ToArray();

        for (var index = 1; index < ratings.Length; index++)
        {
            Assert.True(ratings[index] > ratings[index - 1]);
        }
    }

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(40)]
    public void ContrastIsSymmetricAroundFifty(double lowerRawRating)
    {
        var lower = CalculateForRaw(lowerRawRating);
        var upper = CalculateForRaw(100 - lowerRawRating);

        Assert.Equal(100, lower.Rating + upper.Rating, precision: 10);
    }

    [Fact]
    public void ZeroContrastReturnsRawRating()
    {
        var calculator = new DownhillRatingCalculator(new RatingOptions
        {
            RatingContrast = 0
        });
        var result = calculator.Calculate(82, 62);

        Assert.Equal(result.RawRating, result.Rating, precision: 10);
    }

    [Fact]
    public void RatingAppliesFullContrastIndependentlyOfEvidence()
    {
        var calculator = new DownhillRatingCalculator();
        var result = calculator.Calculate(80, 80);

        Assert.Equal(
            calculator.ApplyRatingContrast(result.RawRating),
            result.Rating,
            precision: 10);
    }

    [Theory]
    [InlineData(-100, -100)]
    [InlineData(150, 150)]
    [InlineData(150, -100)]
    public void RatingAlwaysRemainsWithinZeroToOneHundred(
        double pace,
        double consistency)
    {
        var result = new DownhillRatingCalculator().Calculate(
            pace,
            consistency);

        Assert.InRange(result.RawRating, 0, 100);
        Assert.InRange(result.Rating, 0, 100);
    }

    [Fact]
    public void RawRatingNinetyOneProducesRatingAroundNinetyFive()
    {
        var result = CalculateForRaw(91);

        Assert.Equal(91, result.RawRating, precision: 10);
        Assert.InRange(result.Rating, 94, 96);
    }

    [Fact]
    public void RawRatingFortyProducesRatingBelowForty()
    {
        var result = CalculateForRaw(40);

        Assert.True(result.Rating < 40);
    }

    [Fact]
    public void PaceAndConsistencyAreNotContrastedSeparately()
    {
        var calculator = new DownhillRatingCalculator();
        var result = calculator.Calculate(90, 10);
        var expectedRawRating = (0.70 * 90 + 0.15 * 10) / 0.85;
        var expectedFromRawRating = calculator.ApplyRatingContrast(expectedRawRating);
        var separatelyContrasted =
            (0.70 / 0.85) * calculator.ApplyRatingContrast(90) +
            (0.15 / 0.85) * calculator.ApplyRatingContrast(10);

        Assert.Equal(expectedRawRating, result.RawRating, precision: 10);
        Assert.Equal(expectedFromRawRating, result.Rating, precision: 10);
        Assert.NotEqual(separatelyContrasted, result.Rating, precision: 6);
    }

    private static RatingResult CalculateForRaw(double rawRating)
    {
        return new DownhillRatingCalculator().Calculate(
            pace: rawRating,
            consistency: rawRating);
    }
}
