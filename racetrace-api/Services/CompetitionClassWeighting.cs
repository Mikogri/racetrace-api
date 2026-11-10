namespace RaceTrace.Api.Services;

internal sealed class CompetitionClassWeighting
{
    private readonly IReadOnlyDictionary<int, double> _weights;

    private CompetitionClassWeighting(IReadOnlyDictionary<int, double> weights)
    {
        _weights = weights;
    }

    public static CompetitionClassWeighting FromPriorities(IEnumerable<int?> priorities)
    {
        var activePriorities = priorities
            .Where(priority => priority is > 0)
            .Select(priority => priority!.Value)
            .Distinct()
            .ToArray();

        if (activePriorities.Length == 0)
        {
            return new CompetitionClassWeighting(new Dictionary<int, double>());
        }

        var lowestClassWeight = activePriorities.Max();
        return new CompetitionClassWeighting(
            activePriorities.ToDictionary(
                priority => priority,
                priority => (double)lowestClassWeight / priority
            )
        );
    }

    public double GetWeight(int? priority)
    {
        return priority is > 0 && _weights.TryGetValue(priority.Value, out var weight)
            ? weight
            : 1.0;
    }
}
