using RaceTrace.Api.Infrastructure.Calculations;

namespace RaceTrace.Api.Tests;

public sealed class CalculationIdentityTests
{
    [Fact]
    public void CachedCalculationKeepsItsOriginalComputedAt()
    {
        var provenance = new CalculationProvenance();
        var original = DateTimeOffset.Parse("2026-10-10T12:00:00Z");
        provenance.Include(new("rating=1", "config", "revision", original));
        provenance.Include(new("rating=1", "config", "revision", original.AddMinutes(1)));
        Assert.Equal(original, provenance.OldestComputedAt);
    }

    [Fact]
    public void EffectiveIncidentThresholdsAndSimulationSeedAreInManifest()
    {
        var manifest = System.Text.Json.JsonSerializer.Serialize(AlgorithmCatalog.Current.Manifest);
        Assert.Contains("LargeLossSeconds", manifest);
        Assert.Contains("RandomSeed", manifest);
        Assert.Contains("MonteCarloIterations", manifest);
        Assert.Contains("consistency=", AlgorithmCatalog.Current.VersionLabel("fantasy"));
    }

    [Fact]
    public void ConfigurationHashIsIndependentOfPropertyOrderButChangesWithWeights()
    {
        Assert.Equal(
            AlgorithmCatalog.Hash(new { a = 1, b = 2 }),
            AlgorithmCatalog.Hash(new { b = 2, a = 1 })
        );
        Assert.NotEqual(
            AlgorithmCatalog.Hash(new { final = 4.0 }),
            AlgorithmCatalog.Hash(new { final = 3.0 })
        );
    }

    [Fact]
    public void DependencyChangesInvalidateDependentsButNotUnrelatedAlgorithms()
    {
        var definitions = new Dictionary<string, AlgorithmDefinition>
        {
            ["pace"] = new("1", new { weight = 4 }, []),
            ["consistency"] = new("1", new { weight = 4 }, []),
            ["rating"] = new("1", new { weight = .7 }, ["pace", "consistency"]),
            ["fantasy"] = new("1", new { seed = 12 }, ["rating"]),
        };
        var old = new AlgorithmCatalog(definitions);
        var rating = old.Fingerprint("rating");
        var fantasy = old.Fingerprint("fantasy");
        var pace = old.Fingerprint("pace");
        var updated = new AlgorithmCatalog(
            new Dictionary<string, AlgorithmDefinition>(definitions)
            {
                ["consistency"] = new("2", new { weight = 4 }, []),
            }
        );
        Assert.NotEqual(rating, updated.Fingerprint("rating"));
        Assert.NotEqual(fantasy, updated.Fingerprint("fantasy"));
        Assert.Equal(pace, updated.Fingerprint("pace"));
    }

    [Fact]
    public void RevisionOfUnrelatedSeasonDoesNotInvalidateSelectedSeason()
    {
        var first = new DataRevisionSnapshot(
            new Dictionary<string, long>
            {
                ["global"] = 1,
                ["all"] = 2,
                ["season:2025"] = 1,
                ["season:2026"] = 1,
            }
        );
        var second = new DataRevisionSnapshot(
            new Dictionary<string, long>
            {
                ["global"] = 1,
                ["all"] = 3,
                ["season:2025"] = 1,
                ["season:2026"] = 2,
            }
        );
        Assert.Equal(first.ForScopes(["season:2025"]), second.ForScopes(["season:2025"]));
        Assert.NotEqual(first.ForScopes(["season:2026"]), second.ForScopes(["season:2026"]));
        Assert.NotEqual(first.ForScopes(["all"]), second.ForScopes(["all"]));
    }
}
