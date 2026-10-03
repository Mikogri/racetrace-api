using System.Text.Json;
using System.Text.Json.Nodes;
using UCI_Stats_API.Infrastructure.Supabase;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Tests;

public sealed class PreparedRankingSessionTests
{
    [Fact]
    public void UnavailableFinishReferenceIsExplicitlyNullAndStillComplete()
    {
        var package = JsonNode.Parse("""
        {"r":[[1,18,10]],"a":[[1,10]],"pf":[[10,18,null,null,null,null,null,null,null,null,null]]}
        """)!.AsObject();
        Assert.True(CompactMetricsPackageParser.HasCompletePreparedFinishes(package));
    }

    [Fact]
    public void PreparedFinishSurvivesWithoutTransferringRivalPopulation()
    {
        var package = JsonNode.Parse("""
            {"s":[[10,20,30,"Final","FINAL","2026-06-01",40,2026,"2026-06-01",2,"EM"]],
             "r":[[1,1,10,250000,null,1,true,"Finished",100,null]],
             "a":[[1,10,2,null]], "q":[[1,0,50,1]],
             "g":[[10,0,2,52,3.95,0.02,1,50,54]],
             "h":[[10,0,null,null,null,null,null,null,2,260,5.56,0.02,1,250,270]],
             "pf":[[10,1,249.125,0.875,2,259.125,5.55,0.03,1.25,249.125,269.125]]}
            """)!.AsObject();
        var result = CompactMetricsPackageParser.ParseRiderDetailDataBundle(package);
        var finish = Assert.Single(result.PaceSessions).PaceFinish;
        Assert.NotNull(finish);
        Assert.Equal(249.125, finish.TimeSeconds);
        Assert.Equal(0.875, finish.RemovedLossSeconds);
        Assert.Equal(259.125, finish.Statistics.Median);
        Assert.Equal(250, result.PaceSessions[0].Run.FinishTimeSeconds);
        var roundTrip = JsonSerializer.Deserialize<PaceFinishReference>(JsonSerializer.Serialize(finish));
        Assert.Equal(finish, roundTrip);
    }
}
