using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure.Supabase;

namespace RaceTrace.Api.Tests;

public sealed class RankingReadParityTests
{
    [Fact]
    public void BatchedSourcePreparationPreservesIndependentSessionFields()
    {
        static JsonObject Package(int session, int offset) =>
            JsonNode
                .Parse(
                    $$"""
                    {"v":3,"s":[[{{session}},{{session}},1,"Final","FINAL","2026-06-01",1,2026,"2026-06-01",1,"EM"]],
                     "d":[[1,1001,"A","Rider","FRA",null,null,null],[2,1002,"B","Rider","FRA",null,null,null],[3,1003,"C","Rider","FRA",null,null,null]],
                     "r":[[{{offset + 1}},1,{{session}},100000,null,1,true,"OK",10,null],[{{offset
                        + 2}},2,{{session}},110000,null,2,true,"OK",5,null],[{{offset
                        + 3}},3,{{session}},120000,null,3,true,"OK",1,null]],
                     "ix":[[{{offset + 1}},1,40000],[{{offset + 2}},1,45000],[{{offset
                        + 3}},1,50000]]}
                    """
                )!
                .AsObject();
        var packages = new[] { Package(10, 0), Package(11, 100) };
        var independent = packages
            .SelectMany(package =>
                CompactMetricsPackageParser.ParsePreparedRankingRead([package]).PaceSessions
            )
            .ToArray();
        var read = CompactMetricsPackageParser.ParseRankingReadData(packages);
        var batched = CompactMetricsPackageParser.ParsePreparedRankingRead(packages, read);
        Assert.Same(read, batched.Data);
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(independent),
            System.Text.Json.JsonSerializer.Serialize(batched.PaceSessions)
        );
    }

    [Fact]
    public void FieldRowsKeepTheirLegacyShapeWithoutRiderOrTeamMetadata()
    {
        var package = JsonNode
            .Parse(
                """
                {"f":[[1,2,3,40000,null,1,true,"Finished"]]}
                """
            )!
            .AsObject();
        var row = CompactMetricsPackageParser
            .ParseRankingReadData([package])
            .ToLegacy()
            .FieldResults.Single();
        Assert.False(row.ContainsKey("riders"));
        Assert.False(row.ContainsKey("teams"));
        Assert.True(row.ContainsKey("sessions"));
    }

    [Fact]
    public void ReadRowsShareMetadataAndLegacyConsumersGetIndependentObjects()
    {
        var package = JsonNode
            .Parse(
                """
                {"s":[[10,20,30,"Final","FINAL","2026-06-01",40,2026,"2026-06-01",2,"EM"]],
                "d":[[1,1001,"A","Rider","FRA",null,null,null]],
                "r":[[1,1,10,250000,null,1,true,"Finished",100,null],[2,1,10,260000,null,2,true,"Finished",90,null]]}
                """
            )!
            .AsObject();
        var read = CompactMetricsPackageParser.ParseRankingReadData([package]);
        Assert.Same(read.Results[0].Object("riders"), read.Results[1].Object("riders"));
        Assert.Same(read.Results[0].Object("sessions"), read.Results[1].Object("sessions"));
        var legacy = read.ToLegacy();
        Assert.NotSame(legacy.Results[0]["riders"], legacy.Results[1]["riders"]);
        Assert.Equal(100m, legacy.PointsByResultId["1"]);
        Assert.Equal(260000, read.Results[1].Int("race_time_ms"));
    }
}
