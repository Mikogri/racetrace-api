using System.Text.Json;
using System.Text.Json.Nodes;
using UCI_Stats_API.Infrastructure.Supabase;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Tests;

public sealed class CompactPackageSequenceTests
{
    [Fact]
    public void SequenceMatchesMergedPayloadAndKeepsLastIdentityAcrossSessions()
    {
        JsonObject Package(int session, string name, string team) => new()
        {
            ["s"] = new JsonArray(new JsonArray(session, 20, 30, "Final", "FINAL", "2026-06-01", 40, 2026,
                "2026-06-01", 2, "EM")),
            ["d"] = new JsonArray(new JsonArray(1, 1001, name, "Rider", "FRA", null, 60, team)),
            ["t"] = new JsonArray(new JsonArray(60, team, "TEAM")),
            ["r"] = new JsonArray(new JsonArray(session, 1, session, 250000, null, 1, true, "Finished", 100, 60)),
            ["x"] = new JsonArray(new JsonArray(session, 1, 50000, null)),
            ["a"] = new JsonArray(new JsonArray(session, session, 1, null)),
            ["q"] = new JsonArray(new JsonArray(session, 0, 50, 1)),
            ["g"] = new JsonArray(new JsonArray(session, 0, 1, 50, Math.Log(50), .02, 1, 50, 50)),
            ["h"] = new JsonArray(new JsonArray(session, 0, null, null, null, null, null, null,
                1, 250, Math.Log(250), .02, 1, 250, 250))
        };
        JsonObject[] packages = [Package(10, "Old", "Old Team"), Package(11, "New", "New Team")];
        var original = JsonSerializer.Serialize(packages);
        var merged = new JsonObject();
        foreach (var key in new[] { "s", "d", "t", "r", "f", "x", "a", "q", "g", "h" })
            merged[key] = new JsonArray(packages.SelectMany(p => p[key]?.AsArray() ?? [])
                .Select(row => row?.DeepClone()).ToArray());
        var expected = CompactMetricsPackageParser.ParseRiderDetailDataBundle(merged);
        var actual = CompactMetricsPackageParser.ParseRiderDetailDataBundle(packages);
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(expected), JsonSerializer.SerializeToNode(actual)));
        Assert.All(actual.Data.Results, r => Assert.Equal("New", r.Object("riders")?.String("first_name")));
        Assert.All(actual.Data.Results, r => Assert.Equal("New Team", r.Object("teams")?.String("name")));
        actual.Data.Results[0].Object("riders")!["first_name"] = "Mutated";
        Assert.Equal("New", actual.Data.Results[1].Object("riders")?.String("first_name"));
        Assert.Equal(original, JsonSerializer.Serialize(packages));
    }
}
