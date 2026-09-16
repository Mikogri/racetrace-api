namespace UCI_Stats_API.Features.Fantasy;

public sealed record FantasyCandidate(
    string PinkbikeExternalId,
    string? RiderId,
    string Gender,
    decimal Price,
    decimal ExpectedFantasyPoints);

public sealed record FantasyLineup(
    IReadOnlyList<FantasyCandidate> Riders,
    decimal TotalPrice,
    decimal ExpectedFantasyPoints);

/// <summary>Exact monetary arithmetic: no salary buckets, rounding, or value-based greedy selection.</summary>
public static class FantasyOptimizer
{
    public static FantasyLineup? Optimize(
        IReadOnlyList<FantasyCandidate> candidates,
        FantasyRules rules,
        CancellationToken cancellationToken = default)
    {
        FantasyScoring.ValidateRules(rules);
        if (rules.Men != 4 || rules.Women != 2 || rules.TeamSize != 6)
            throw new ArgumentException("This optimizer supports the verified four-men/two-women roster.", nameof(rules));
        ValidateCandidates(candidates);
        cancellationToken.ThrowIfCancellationRequested();
        var men = candidates.Where(x => x.Gender == "male" && x.Price <= rules.Budget)
            .OrderBy(x => x.PinkbikeExternalId, StringComparer.Ordinal).ToArray();
        var women = candidates.Where(x => x.Gender == "female" && x.Price <= rules.Budget)
            .OrderBy(x => x.PinkbikeExternalId, StringComparer.Ordinal).ToArray();
        if (men.Length < 4 || women.Length < 2) return null;

        var pairs = new List<Pair>();
        for (var i = 0; i < women.Length - 1; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var j = i + 1; j < women.Length; j++)
            {
                var cost = women[i].Price + women[j].Price;
                if (cost <= rules.Budget)
                    pairs.Add(new(women[i], women[j], cost, women[i].ExpectedFantasyPoints + women[j].ExpectedFantasyPoints));
            }
        }
        if (pairs.Count == 0) return null;
        pairs.Sort((left, right) => left.Cost.CompareTo(right.Cost));
        var prefixBest = new Pair[pairs.Count];
        prefixBest[0] = pairs[0];
        for (var i = 1; i < pairs.Count; i++)
            prefixBest[i] = BetterPair(pairs[i], prefixBest[i - 1]) ? pairs[i] : prefixBest[i - 1];

        FantasyLineup? best = null;
        for (var a = 0; a < men.Length - 3; a++)
        for (var b = a + 1; b < men.Length - 2; b++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var c = b + 1; c < men.Length - 1; c++)
            for (var d = c + 1; d < men.Length; d++)
            {
                var maleCost = men[a].Price + men[b].Price + men[c].Price + men[d].Price;
                var pairIndex = LastAffordable(pairs, rules.Budget - maleCost);
                if (pairIndex < 0) continue;
                var pair = prefixBest[pairIndex];
                var totalCost = maleCost + pair.Cost;
                var points = men[a].ExpectedFantasyPoints + men[b].ExpectedFantasyPoints
                    + men[c].ExpectedFantasyPoints + men[d].ExpectedFantasyPoints + pair.Points;
                if (best is null || points > best.ExpectedFantasyPoints
                    || points == best.ExpectedFantasyPoints && totalCost < best.TotalPrice)
                    best = new([men[a], men[b], men[c], men[d], pair.First, pair.Second], totalCost, points);
            }
        }
        return best;
    }

    private static void ValidateCandidates(IReadOnlyList<FantasyCandidate> candidates)
    {
        var externalIds = new HashSet<string>(StringComparer.Ordinal);
        var riderIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.PinkbikeExternalId) || candidate.Price <= 0
                || candidate.ExpectedFantasyPoints < 0 || candidate.Gender is not ("male" or "female")
                || candidate.RiderId is not null && string.IsNullOrWhiteSpace(candidate.RiderId))
                throw new ArgumentException("Invalid candidate identity, price, gender, or expected points.", nameof(candidates));
            if (!externalIds.Add(candidate.PinkbikeExternalId)
                || candidate.RiderId is not null && !riderIds.Add(candidate.RiderId))
                throw new ArgumentException("A rider is present more than once in the candidate pool.", nameof(candidates));
        }
    }

    private static int LastAffordable(List<Pair> pairs, decimal budget)
    {
        var low = 0;
        var high = pairs.Count - 1;
        var found = -1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (pairs[middle].Cost <= budget) { found = middle; low = middle + 1; }
            else high = middle - 1;
        }
        return found;
    }

    private static bool BetterPair(Pair left, Pair right) => left.Points > right.Points
        || left.Points == right.Points && (left.Cost < right.Cost
            || left.Cost == right.Cost && string.CompareOrdinal(left.First.PinkbikeExternalId, right.First.PinkbikeExternalId) < 0
            || left.Cost == right.Cost && left.First.PinkbikeExternalId == right.First.PinkbikeExternalId
                && string.CompareOrdinal(left.Second.PinkbikeExternalId, right.Second.PinkbikeExternalId) < 0);

    private sealed record Pair(FantasyCandidate First, FantasyCandidate Second, decimal Cost, decimal Points);
}
