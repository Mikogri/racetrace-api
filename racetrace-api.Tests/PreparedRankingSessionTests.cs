using System.Text.Json;
using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure.Supabase;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class PreparedRankingSessionTests
{
    [Fact]
    public void InputPackageCalculatesFromMeasurementsWithoutStoredScores()
    {
        var package = JsonNode.Parse("""
            {"v":3,"s":[[10,20,30,"Final","FINAL","2026-06-01",40,2026,"2026-06-01",2,"EM"]],
             "r":[[1,1,10,150000,50,1,true,"OK",100,null]],
             "f":[[1,1,10,150000,50,1,true,"OK"],[2,2,10,153000,49,2,true,"OK"]],
             "ix":[[1,1,50000],[1,2,100000],[2,1,51000],[2,2,102000]],
             "pf":[[10,1,1,149,2,1,0,0.03,1,1,1]]}
            """)!.AsObject();
        var result = CompactMetricsPackageParser.ParseRiderDetailDataBundle(package);
        var session = Assert.Single(result.PaceSessions);
        Assert.Equal(2, session.ClassifiedFieldSize);
        Assert.Equal(new double?[] {50,50,50}, session.Run.SectorTimes);
        Assert.Equal(150, session.PaceFinish!.TimeSeconds);
        Assert.Equal(151.5, session.PaceFinish.Statistics.Median);
    }

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
