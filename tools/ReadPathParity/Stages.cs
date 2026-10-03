using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using UCI_Stats_API.Configuration;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Models;
using UCI_Stats_API.Services;

static class Stages
{
    public static async Task Run(SupabaseOptions options)
    {
        foreach(var years in new[]{"2026","all"})
        foreach(var raw in new[]{false,true})
        {
            using var h=new ProbeHandler(raw);
            using var http=new HttpClient(h){Timeout=TimeSpan.FromSeconds(100)};
            var client=new SupabaseRestClient(http,Options.Create(options));
            using var cache=new MemoryCache(new MemoryCacheOptions());
            var domain=new DomainQueryService(client,packageCache:cache);
            var validator=new QueryValidationService();
            var metrics=new MetricsService(domain,new PaceService(client));
            var ranking=new RankingService(validator,domain,metrics,new RankingSingleFlight(),cache);
            var query=new QueryCollection(QueryHelpers.ParseQuery("?category=elite_men"+(years=="all"?"":"&season="+years)));
            var context=await domain.ParseMetricsContextAsync(query,validator,CancellationToken.None);
            var sessions=await domain.GetMatchingSessionsAsync(context);
            var flags=BindingFlags.NonPublic|BindingFlags.Instance;
            var fetch=typeof(DomainQueryService).GetMethod("GetRankingPreaggregatesAsync",flags)!;
            h.Calls.Clear();
            var watch=Stopwatch.StartNew();
            object? pack=null;
            RankingDataBundle data;
            if(raw) data=await domain.GetRankingDataAsync(context,resolvedSessionIds:sessions.Select(x=>x.String("id")!));
            else {
                var task=(Task)fetch.Invoke(domain,new object?[]{sessions,null,null,SupabaseRole.Anon,CancellationToken.None,false})!;
                await task;
                pack=task.GetType().GetProperty("Result")!.GetValue(task)!;
                data=(RankingDataBundle)pack.GetType().GetProperty("Data")!.GetValue(pack)!;
            }
            var loadMs=watch.Elapsed.TotalMilliseconds;
            Console.WriteLine(JsonSerializer.Serialize(new{years,raw,stage="load-and-parse",ms=loadMs,calls=h.Calls.ToArray(),results=data.Results.Count,splits=data.Splits.Count,sessions=data.Sessions.Count}));
            var perfMethod=typeof(MetricsService).GetMethod("CalculatePrecomputedPerformanceByRider",flags)!;
            var rowMethod=typeof(RankingService).GetMethod("BuildRow",flags)!;
            for(var iteration=1;iteration<=3;iteration++){
                watch.Restart();
                object values=raw
                    ? await metrics.CalculatePerformanceByRiderAsync(data.Results,context,resultsContainFullField:true,availableSessions:sessions,suppliedSplits:data.Splits,suppliedFieldResults:data.FieldResults)
                    : perfMethod.Invoke(metrics,new[]{data.Results,pack!.GetType().GetProperty("PaceSessions")!.GetValue(pack),sessions})!;
                var metricsMs=watch.Elapsed.TotalMilliseconds;
                watch.Restart();
                var rows=data.Results.GroupBy(r=>r.String("rider_id")!).Select(g=>(JsonObject)rowMethod.Invoke(ranking,new object?[]{g.Key,g.ToArray(),data.PointsByResultId,values,null})!).ToArray();
                Console.WriteLine(JsonSerializer.Serialize(new{years,raw,stage="compute",iteration,metricsMs,rowsMs=watch.Elapsed.TotalMilliseconds,riders=rows.Length}));
            }
        }
    }
}
