using RaceTrace.Api.Features.Fantasy;

namespace RaceTrace.Api.Tests;

public sealed class FantasyRuleCatalogTests
{
    [Fact]
    public void VerifiedSeasonRetainsTheOfficialRulesInstance() =>
        Assert.Same(FantasyRules2026.Rules, FantasyRuleCatalog.ForSeason(2026));

    [Theory]
    [InlineData(2025)]
    [InlineData(2027)]
    public void UnverifiedSeasonsDoNotInheritAnotherSeasonsScoring(int season) =>
        Assert.Null(FantasyRuleCatalog.ForSeason(season));

    [Fact]
    public void DefaultPublicRulesRemainUnchanged() =>
        Assert.Same(FantasyRules2026.Rules, FantasyRuleCatalog.Default);
}
