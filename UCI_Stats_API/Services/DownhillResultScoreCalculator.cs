namespace UCI_Stats_API.Services;

internal sealed record RiderResultScore(
    double? Score,
    double Confidence,
    int UsedSessions);

internal sealed class ResultScoreOptions
{
    // Anchors the progressive position curve. Lower values create larger gaps
    // between the winner and the rest of the podium.
    public double SecondPlaceScore { get; init; } = 96.0;
    public double ConfidenceWeightScale { get; init; } = 2.5;
}

internal sealed class DownhillResultScoreCalculator
{
    private readonly ResultScoreOptions _options;

    public DownhillResultScoreCalculator(ResultScoreOptions? options = null)
    {
        _options = options ?? new ResultScoreOptions();
        if (_options.SecondPlaceScore <= 0 ||
            _options.SecondPlaceScore >= 100 ||
            _options.ConfidenceWeightScale <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Second-place score must be between 0 and 100, and confidence scale must be positive.");
        }
    }

    public RiderResultScore Calculate(
        string riderId,
        IEnumerable<PaceSessionData> sessions,
        CompetitionClassWeighting? competitionClassWeighting = null)
    {
        var weightedScores = sessions
            .Select(session => BuildSessionScore(
                riderId,
                session,
                competitionClassWeighting))
            .Where(score => score is not null && score.Weight > 0)
            .Select(score => score!)
            .ToArray();

        if (weightedScores.Length == 0)
        {
            return new RiderResultScore(Score: null, Confidence: 0, UsedSessions: 0);
        }

        var weightSum = weightedScores.Sum(score => score.Weight);
        var resultScore = weightedScores.Sum(score => score.Score * score.Weight) / weightSum;
        var confidence = 1.0 - Math.Exp(-weightSum / _options.ConfidenceWeightScale);

        return new RiderResultScore(
            Score: Clamp(resultScore, 0, 100),
            Confidence: Clamp(confidence, 0, 1),
            UsedSessions: weightedScores.Length);
    }

    public RiderResultScore Calculate(
        IEnumerable<PacePrecomputedSessionData> sessions,
        CompetitionClassWeighting? competitionClassWeighting = null)
    {
        var weightedScores = sessions
            .Select(session => BuildSessionScore(
                session,
                competitionClassWeighting))
            .Where(score => score is not null && score.Weight > 0)
            .Select(score => score!)
            .ToArray();

        if (weightedScores.Length == 0)
        {
            return new RiderResultScore(Score: null, Confidence: 0, UsedSessions: 0);
        }

        var weightSum = weightedScores.Sum(score => score.Weight);
        var resultScore = weightedScores.Sum(score => score.Score * score.Weight) / weightSum;
        var confidence = 1.0 - Math.Exp(-weightSum / _options.ConfidenceWeightScale);

        return new RiderResultScore(
            Score: Clamp(resultScore, 0, 100),
            Confidence: Clamp(confidence, 0, 1),
            UsedSessions: weightedScores.Length);
    }

    internal double CalculatePositionScore(int rank, int fieldSize)
    {
        if (fieldSize < 2 || rank < 1 || rank > fieldSize)
        {
            throw new ArgumentOutOfRangeException(nameof(rank));
        }

        if (rank == 1)
        {
            return 100;
        }

        if (rank == fieldSize)
        {
            return 0;
        }

        var normalizedLogRank = Math.Log(rank) / Math.Log(fieldSize);
        var secondPlaceBase = 1.0 - Math.Log(2) / Math.Log(fieldSize);
        var targetSecondPlace = _options.SecondPlaceScore / 100.0;
        var curveExponent = Math.Log(targetSecondPlace) / Math.Log(secondPlaceBase);

        return 100.0 * Math.Pow(1.0 - normalizedLogRank, curveExponent);
    }

    private WeightedScore? BuildSessionScore(
        string riderId,
        PaceSessionData session,
        CompetitionClassWeighting? competitionClassWeighting)
    {
        var sessionWeight = DownhillPaceCalculator.GetSessionWeight(session.SessionType);
        if (sessionWeight <= 0)
        {
            return null;
        }

        var riderRun = session.Runs.FirstOrDefault(run => string.Equals(
            run.RiderId,
            riderId,
            StringComparison.OrdinalIgnoreCase));
        if (riderRun is null || IsIgnoredStatus(riderRun.Status))
        {
            return null;
        }

        var classified = session.Runs
            .Where(run => run.IsClassified && run.FinishRank is > 0)
            .GroupBy(run => run.RiderId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (classified.Length < 2)
        {
            return null;
        }

        var weight = sessionWeight *
            (competitionClassWeighting?.GetWeight(session.CompetitionClassPriority) ?? 1.0);
        if (IsDidNotFinish(riderRun.Status))
        {
            return new WeightedScore(0, weight);
        }

        var rider = classified.FirstOrDefault(run => string.Equals(
            run.RiderId,
            riderId,
            StringComparison.OrdinalIgnoreCase));
        if (rider?.FinishRank is not { } rank || rank > classified.Length)
        {
            return null;
        }

        return new WeightedScore(
            CalculatePositionScore(rank, classified.Length),
            weight);
    }

    private WeightedScore? BuildSessionScore(
        PacePrecomputedSessionData session,
        CompetitionClassWeighting? competitionClassWeighting)
    {
        var sessionWeight = DownhillPaceCalculator.GetSessionWeight(session.SessionType);
        if (sessionWeight <= 0 ||
            session.ClassifiedFieldSize < 2 ||
            IsIgnoredStatus(session.Run.Status))
        {
            return null;
        }

        var weight = sessionWeight *
            (competitionClassWeighting?.GetWeight(session.CompetitionClassPriority) ?? 1.0);
        if (IsDidNotFinish(session.Run.Status))
        {
            return new WeightedScore(0, weight);
        }

        if (!session.Run.IsClassified ||
            session.Run.FinishRank is not { } rank ||
            rank > session.ClassifiedFieldSize)
        {
            return null;
        }

        return new WeightedScore(
            CalculatePositionScore(rank, session.ClassifiedFieldSize),
            weight);
    }

    private static bool IsIgnoredStatus(string? status)
    {
        return string.Equals(status, "DNS", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "DSQ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDidNotFinish(string? status)
    {
        return string.Equals(status, "DNF", StringComparison.OrdinalIgnoreCase);
    }

    private static double Clamp(double value, double minimum, double maximum)
    {
        return Math.Min(maximum, Math.Max(minimum, value));
    }
}
