namespace RaceTrace.Api.Features.Fantasy;

public sealed record FantasyRules(
    int Season,
    string Version,
    decimal Budget,
    int TeamSize,
    int Men,
    int Women,
    IReadOnlyDictionary<int, int> MenFinalPlacementPoints,
    IReadOnlyDictionary<int, int> WomenFinalPlacementPoints,
    IReadOnlyDictionary<int, int> Q1Bonus
);

/// <summary>Unconditional position probabilities; non-classification is not last place.</summary>
public sealed record FantasyPositionDistribution(
    IReadOnlyDictionary<int, decimal> PositionProbabilities,
    decimal NonScoringProbability
);

public sealed record FantasyPointsProjection(
    decimal ExpectedFinalPoints,
    decimal ExpectedQ1Bonus,
    decimal ExpectedFantasyPoints
);

public enum FantasyQ1Retention
{
    Unverified,
    Retained,
    Forfeited,
}

public static class FantasyScoring
{
    /// <summary>Expected classified position, conditional on finishing with a known positive position.</summary>
    public static decimal? ExpectedClassifiedPosition(FantasyPositionDistribution distribution)
    {
        ValidateDistribution(distribution);
        var classifiedMass = distribution.PositionProbabilities.Values.Sum();
        return classifiedMass == 0
            ? null
            : distribution.PositionProbabilities.Sum(pair => pair.Key * pair.Value)
                / classifiedMass;
    }

    public static FantasyPointsProjection Project(
        FantasyPositionDistribution final,
        FantasyPositionDistribution? q1,
        FantasyRules rules,
        bool q1BonusApplies,
        string gender
    )
    {
        ValidateRules(rules);
        var finalPoints = ExpectedPoints(final, FinalPointsFor(rules, gender));
        if (q1BonusApplies && q1 is null)
            throw new ArgumentException(
                "A Q1 distribution is required when its bonus applies.",
                nameof(q1)
            );
        var q1Points = q1BonusApplies ? ExpectedPoints(q1!, rules.Q1Bonus) : 0m;
        return new(finalPoints, q1Points, finalPoints + q1Points);
    }

    public static int ActualPoints(
        int? finalPosition,
        string? finalStatus,
        int? q1Position,
        bool q1BonusApplies,
        FantasyQ1Retention nonScoringFinalQ1Retention,
        FantasyRules rules,
        string gender
    )
    {
        ValidateRules(rules);
        var status = finalStatus?.Trim().ToUpperInvariant();
        var noFinalScore =
            status is "DNS" or "DNF" or "DSQ" or "DNQ" or "NOT_STARTED" or "NOT_QUALIFIED"
            || finalPosition is null or <= 0;
        var finalTable = FinalPointsFor(rules, gender);
        var finalPoints = noFinalScore ? 0 : finalTable.GetValueOrDefault(finalPosition!.Value);
        var bonus =
            q1BonusApplies && q1Position is > 0
                ? rules.Q1Bonus.GetValueOrDefault(q1Position.Value)
                : 0;
        if (noFinalScore && bonus > 0)
        {
            // The caller must provide verified rules; do not invent DNF/Q1 policy.
            if (nonScoringFinalQ1Retention == FantasyQ1Retention.Unverified)
                throw new InvalidOperationException(
                    "Q1 retention for a non-scoring final has not been verified."
                );
            if (nonScoringFinalQ1Retention == FantasyQ1Retention.Forfeited)
                bonus = 0;
        }
        return checked(finalPoints + bonus);
    }

    internal static void ValidateRules(FantasyRules rules)
    {
        if (
            rules.Season <= 0
            || string.IsNullOrWhiteSpace(rules.Version)
            || rules.Budget <= 0
            || rules.Men <= 0
            || rules.Women <= 0
            || rules.TeamSize != rules.Men + rules.Women
            || rules.MenFinalPlacementPoints.Count == 0
            || rules.WomenFinalPlacementPoints.Count == 0
            || rules.Q1Bonus.Count == 0
            || rules
                .MenFinalPlacementPoints.Concat(rules.WomenFinalPlacementPoints)
                .Concat(rules.Q1Bonus)
                .Any(x => x.Key <= 0 || x.Value < 0)
        )
            throw new ArgumentException("Invalid or incomplete Fantasy rules.", nameof(rules));
    }

    private static IReadOnlyDictionary<int, int> FinalPointsFor(
        FantasyRules rules,
        string gender
    ) =>
        gender switch
        {
            "male" => rules.MenFinalPlacementPoints,
            "female" => rules.WomenFinalPlacementPoints,
            _ => throw new ArgumentException(
                "A verified male/female category is required for scoring.",
                nameof(gender)
            ),
        };

    private static decimal ExpectedPoints(
        FantasyPositionDistribution distribution,
        IReadOnlyDictionary<int, int> points
    )
    {
        ValidateDistribution(distribution);
        return distribution.PositionProbabilities.Sum(x =>
            x.Value * points.GetValueOrDefault(x.Key)
        );
    }

    private static void ValidateDistribution(FantasyPositionDistribution distribution)
    {
        if (
            distribution.NonScoringProbability is < 0 or > 1
            || distribution.PositionProbabilities.Any(x => x.Key <= 0 || x.Value is < 0 or > 1)
        )
            throw new ArgumentException(
                "Position probabilities must be non-negative with positive position keys.",
                nameof(distribution)
            );
        var mass =
            distribution.NonScoringProbability + distribution.PositionProbabilities.Values.Sum();
        if (Math.Abs(mass - 1m) > 0.000000001m)
            throw new ArgumentException(
                "A full distribution must account for all scoring and non-scoring probability mass.",
                nameof(distribution)
            );
    }
}
