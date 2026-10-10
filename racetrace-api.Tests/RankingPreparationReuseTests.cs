using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using RaceTrace.Api.Configuration;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class RankingPreparationReuseTests
{
    [Fact]
    public async Task SharedPackageReusesPreparationButReplacementAndRequestDoNot()
    {
        var row = JsonNode.Parse("""
        {"session_id":10,"payload":{
        "s":[[10,20,30,"Final","FINAL","2026-06-01",40,2026,"2026-06-01",2,"EM"]],
        "d":[[1,1001,"A","Rider","FRA"]],
        "r":[[1,1,10,250000,null,1,true,"Finished",100,null]],
        "a":[[1,10,1,null]],"q":[],"h":[],"g":[]}}
        """)!.AsObject();
        var original = row.ToJsonString();
        using var memory = new MemoryCache(new MemoryCacheOptions());
        const string key = "ranking-package:inputs-metrics:Anon:10";
        memory.Set(key, row);
        using var http = new HttpClient();
        var client = new SupabaseRestClient(http, Options.Create(new SupabaseOptions { Url = "https://test.invalid", AnonKey = "test" }));
        DomainQueryService Domain() => new(client, packageCache: memory,
            readOptions: Options.Create(new ReadOptimizationOptions { TypedRanking = true }));
        var domain = Domain();
        JsonObject[] sessions = [new() { ["id"] = 10 }];
        var first = await domain.GetRankingPreaggregatesAsync(sessions, includeSplits: false);
        var second = await domain.GetRankingPreaggregatesAsync(sessions, riderQuery: "Rider", includeSplits: false);
        Assert.Same(first!.PaceSessions.Single(), second!.PaceSessions.Single());
        Assert.Equal(original, row.ToJsonString());

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => domain.GetRankingPreaggregatesAsync(
            sessions, cancellationToken: canceled.Token, includeSplits: false));
        memory.Set(key, row.DeepClone().AsObject());
        var replacement = await domain.GetRankingPreaggregatesAsync(sessions, includeSplits: false);
        Assert.NotSame(first.PaceSessions[0], replacement!.PaceSessions[0]);
        var otherRequest = await Domain().GetRankingPreaggregatesAsync(sessions, includeSplits: false);
        Assert.NotSame(replacement.PaceSessions[0], otherRequest!.PaceSessions[0]);
    }
}
