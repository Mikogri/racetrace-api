using System.Collections.Concurrent;
using RaceTrace.Api.Infrastructure.Calculations;

namespace RaceTrace.Api.Services;

internal sealed record RiderResultScore(double? Score, double Confidence, int UsedSessions);

internal sealed record PreparedResultScoreSession(
    PaceSessionType SessionType,
    int? CompetitionClassPriority,
    ILookup<string, PaceRiderRun> Runs,
    int ClassifiedFieldSize
);

internal sealed class DownhillResultScoreCalculator
{
    private readonly ResultScoreOptions _options;
    private readonly SessionWeightOptions _sessionWeights;
    private readonly double _logSecondPlace;
    private readonly ConcurrentDictionary<int, (double LogSize, double Exponent)> _curves = new();

    public DownhillResultScoreCalculator(
        ResultScoreOptions? options = null,
        SessionWeightOptions? sessionWeights = null
    )
    {
        _options = options ?? AlgorithmConfiguration.ResultScore;
        _sessionWeights = sessionWeights ?? AlgorithmConfiguration.SessionWeights;
        if (
            _options.SecondPlaceScore <= 0
            || _options.SecondPlaceScore >= 100
            || _options.ConfidenceWeightScale <= 0
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Second-place score must be between 0 and 100, and confidence scale must be positive."
            );
        }
        _logSecondPlace = Math.Log(_options.SecondPlaceScore / 100.0);
    }

    public RiderResultScore Calculate(
        string riderId,
        IEnumerable<PaceSessionData> sessions,
        CompetitionClassWeighting? competitionClassWeighting = null
    ) =>
        Aggregate(
            sessions.Select(session =>
                (
                    session.SessionType,
                    BuildSessionScore(riderId, session, competitionClassWeighting)
                )
            )
        );

    internal RiderResultScore CalculatePrepared(
        string riderId,
        IEnumerable<PreparedResultScoreSession> sessions,
        CompetitionClassWeighting? competitionClassWeighting = null
    ) =>
        Aggregate(
            sessions.Select(session =>
                (
                    session.SessionType,
                    BuildSessionScore(riderId, session, competitionClassWeighting)
                )
            )
        );

    public RiderResultScore Calculate(
        IEnumerable<PacePrecomputedSessionData> sessions,
        CompetitionClassWeighting? competitionClassWeighting = null
    ) =>
        Aggregate(
            sessions.Select(session =>
                (session.SessionType, BuildSessionScore(session, competitionClassWeighting))
            )
        );

    private RiderResultScore Aggregate(
        IEnumerable<(PaceSessionType SessionType, WeightedScore? Score)> scores
    )
    {
        var sessions = scores.Where(item => item.Score is { Weight: > 0 }).ToArray();
        if (sessions.Length == 0)
            return new(null, 0, 0);

        var types = new PaceSessionType[sessions.Length];
        var weights = new double[sessions.Length];
        double evidenceWeightSum = 0;
        for (var i = 0; i < sessions.Length; i++)
        {
            types[i] = sessions[i].SessionType;
            weights[i] = sessions[i].Score!.Weight;
            evidenceWeightSum += weights[i];
        }
        var effectiveWeights = _sessionWeights.ApplyFinalTargetImpact(types, weights);
        double weightSum = 0,
            weightedScoreSum = 0;
        for (var i = 0; i < sessions.Length; i++)
        {
            weightSum += effectiveWeights[i];
            weightedScoreSum += sessions[i].Score!.Score * effectiveWeights[i];
        }
        var confidence = 1.0 - Math.Exp(-evidenceWeightSum / _options.ConfidenceWeightScale);
        return new(
            Clamp(weightedScoreSum / weightSum, 0, 100),
            Clamp(confidence, 0, 1),
            sessions.Length
        );
    }

    internal static PreparedResultScoreSession PrepareSession(PaceSessionData session)
    {
        var runs = session.Runs.ToLookup(run => run.RiderId, StringComparer.OrdinalIgnoreCase);
        return new(
            session.SessionType,
            session.CompetitionClassPriority,
            runs,
            runs.Count(group => group.Any(IsClassifiedForScore))
        );
    }

    private static bool IsClassifiedForScore(PaceRiderRun run) =>
        !DownhillPaceCalculator.IsDisqualified(run.Status)
        && run.IsClassified
        && run.FinishRank is > 0;

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

        var curve = _curves.GetOrAdd(
            fieldSize,
            static (size, logSecondPlace) =>
            {
                var logSize = Math.Log(size);
                var secondPlaceBase = 1.0 - Math.Log(2) / logSize;
                return (logSize, logSecondPlace / Math.Log(secondPlaceBase));
            },
            _logSecondPlace
        );
        var normalizedLogRank = Math.Log(rank) / curve.LogSize;
        return 100.0 * Math.Pow(1.0 - normalizedLogRank, curve.Exponent);
    }

    private WeightedScore? BuildSessionScore(
        string riderId,
        PaceSessionData session,
        CompetitionClassWeighting? competitionClassWeighting
    )
    {
        var sessionWeight = _sessionWeights.GetWeight(session.SessionType);
        if (sessionWeight <= 0)
            return null;

        var riderRun = session.Runs.FirstOrDefault(run =>
            string.Equals(run.RiderId, riderId, StringComparison.OrdinalIgnoreCase)
        );
        if (riderRun is null || IsIgnoredStatus(riderRun.Status))
            return null;

        var classifiedRiders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int? rank = null;
        foreach (var run in session.Runs)
        {
            if (!IsClassifiedForScore(run) || !classifiedRiders.Add(run.RiderId))
                continue;
            if (string.Equals(run.RiderId, riderId, StringComparison.OrdinalIgnoreCase))
                rank = run.FinishRank;
        }
        if (classifiedRiders.Count < 2)
            return null;

        var weight =
            sessionWeight
            * (competitionClassWeighting?.GetWeight(session.CompetitionClassPriority) ?? 1.0);
        if (IsDidNotFinish(riderRun.Status))
            return new WeightedScore(0, weight);
        if (rank is not { } finishRank || finishRank > classifiedRiders.Count)
            return null;
        return new WeightedScore(
            CalculatePositionScore(finishRank, classifiedRiders.Count),
            weight
        );
    }

    private WeightedScore? BuildSessionScore(
        string riderId,
        PreparedResultScoreSession session,
        CompetitionClassWeighting? competitionClassWeighting
    )
    {
        var sessionWeight = _sessionWeights.GetWeight(session.SessionType);
        if (sessionWeight <= 0)
        {
            return null;
        }

        var riderRuns = session.Runs[riderId];
        var riderRun = riderRuns.FirstOrDefault();
        if (riderRun is null || IsIgnoredStatus(riderRun.Status))
        {
            return null;
        }

        if (session.ClassifiedFieldSize < 2)
            return null;

        var weight =
            sessionWeight
            * (competitionClassWeighting?.GetWeight(session.CompetitionClassPriority) ?? 1.0);
        if (IsDidNotFinish(riderRun.Status))
        {
            return new WeightedScore(0, weight);
        }

        var rider = riderRuns.FirstOrDefault(IsClassifiedForScore);
        if (rider?.FinishRank is not { } rank || rank > session.ClassifiedFieldSize)
        {
            return null;
        }

        return new WeightedScore(CalculatePositionScore(rank, session.ClassifiedFieldSize), weight);
    }

    private WeightedScore? BuildSessionScore(
        PacePrecomputedSessionData session,
        CompetitionClassWeighting? competitionClassWeighting
    )
    {
        var sessionWeight = _sessionWeights.GetWeight(session.SessionType);
        if (
            sessionWeight <= 0
            || session.ClassifiedFieldSize < 2
            || IsIgnoredStatus(session.Run.Status)
        )
        {
            return null;
        }

        var weight =
            sessionWeight
            * (competitionClassWeighting?.GetWeight(session.CompetitionClassPriority) ?? 1.0);
        if (IsDidNotFinish(session.Run.Status))
        {
            return new WeightedScore(0, weight);
        }

        if (
            !session.Run.IsClassified
            || session.Run.FinishRank is not { } rank
            || rank > session.ClassifiedFieldSize
        )
        {
            return null;
        }

        return new WeightedScore(CalculatePositionScore(rank, session.ClassifiedFieldSize), weight);
    }

    private static bool IsIgnoredStatus(string? status)
    {
        return string.Equals(status?.Trim(), "DNS", StringComparison.OrdinalIgnoreCase)
            || DownhillPaceCalculator.IsDisqualified(status);
    }

    private static bool IsDidNotFinish(string? status)
    {
        return string.Equals(status?.Trim(), "DNF", StringComparison.OrdinalIgnoreCase);
    }

    private static double Clamp(double value, double minimum, double maximum)
    {
        return Math.Min(maximum, Math.Max(minimum, value));
    }
}
