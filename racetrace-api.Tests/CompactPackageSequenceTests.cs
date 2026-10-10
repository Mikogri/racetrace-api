using System.Text.Json;
using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure.Supabase;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class CompactPackageSequenceTests
{
    [Theory]
    [InlineData("s")]
    [InlineData("f")]
    [InlineData("r")]
    [InlineData("all")]
    public void InputPackagesRejectMissingIdentifiers(string key)
    {
        var package = JsonNode.Parse("""
        {"v":3,"s":[[1,1,1,"Final","FINAL"]],"r":[[2,3,1,100,null,1,true,"OK"]],"f":[[2,3,1,100,null,1,true,"OK"]],"ix":[[2,1,100]],"x":[]}
        """)!.AsObject();
        Assert.True(CompactMetricsPackageParser.HasCompleteInputMeasurements(package));
        if (key == "all")
        {
            foreach (var table in new[] { "s", "r", "f", "ix" }) package[table]![0]![0] = null;
            package["f"]![0]![2] = null;
        }
        else package[key]![0]![0] = null;
        Assert.False(CompactMetricsPackageParser.HasCompleteInputMeasurements(package));
    }

    [Fact]
    public void IdentitySnapshotsUseRaceDateRatherThanImportId()
    {
        var newer = JsonNode.Parse("""
        {"s":[[1,1,1,"Final","FINAL","2026-01-01"]],"d":[[1,1,"Name","Rider","FRA",null,2,"Current Team"]],"r":[[1,1,1,100,null,1,true,"OK"]]}
        """)!.AsObject();
        var older = JsonNode.Parse("""
        {"s":[[99,2,1,"Final","FINAL","2022-01-01"]],"d":[[1,1,"Name","Rider","FRA",null,1,"Old Team"]],"r":[[99,1,99,100,null,1,true,"OK"]]}
        """)!.AsObject();
        var data = CompactMetricsPackageParser.ParseRankingReadData([newer, older]);
        Assert.All(data.Results, row => Assert.Equal("Current Team", row.Rider!["teams"]![0]!["name"]!.ToString()));
    }

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
