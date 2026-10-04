namespace RaceTrace.Api.Models;

public sealed record PageRequest(int Page, int Limit)
{
    public int Offset => (Page - 1) * Limit;
}

public sealed record SortRequest(string Sort, string Order)
{
    public bool Descending => string.Equals(Order, "desc", StringComparison.OrdinalIgnoreCase);
}
