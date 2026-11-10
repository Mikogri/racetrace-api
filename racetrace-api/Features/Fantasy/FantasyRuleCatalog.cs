namespace RaceTrace.Api.Features.Fantasy;

internal static class FantasyRuleCatalog
{
    internal static FantasyRules Default => FantasyRules2026.Rules;

    // Register verified seasons only; historical training can still use the target season's rules.
    internal static FantasyRules? ForSeason(int season) =>
        season switch
        {
            2026 => FantasyRules2026.Rules,
            _ => null,
        };
}
