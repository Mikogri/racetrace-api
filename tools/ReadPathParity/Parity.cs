using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using RaceTrace.Api.Configuration;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Services;

static class Parity {
 public static async Task Run(SupabaseOptions options,string[] args) {
  var capture=args.Contains("--capture");
  var dir=Path.GetFullPath(args[Array.IndexOf(args,"--output")+1]);
  Directory.CreateDirectory(dir);
  var cases=new[]{
   ("rank-2026","", "?season=2026&category=elite_men&sort=rating&pageSize=150&comparison=previousRound"),
   ("rank-all","", "?category=elite_men&sort=rating&pageSize=150&comparison=previousRound"),
   ("rank-ew","", "?season=2025&category=elite_women&sort=pace&pageSize=150&comparison=previousFilters&comparisonSeason=2026&comparisonCategory=elite_women"),
   ("rank-jm","", "?season=2025,2026&category=junior_men&sort=consistency&pageSize=150"),
   ("rank-jw","", "?season=2026&category=junior_women&sort=points&pageSize=150"),
   ("rank-wch","", "?category=elite_men&class=world_championship&sort=rating&pageSize=150"),
   ("rank-date","", "?category=elite_men&dateFrom=2024-01-01&dateTo=2024-08-01&sessionType=FINAL&sort=rating&pageSize=150"),
   ("rank-empty","", "?season=2099&category=elite_men&sort=rating&pageSize=150"),
   ("bruni-all","18","?category=elite_men"),
   ("bruni-finals","18","?season=2026&category=elite_men&sessionType=FINAL"),
   ("asa","183","?season=2026&category=elite_men"),
   ("jordan","60","?season=2025,2026&category=elite_men"),
   ("rider-empty","18","?season=2099&category=elite_men"),
   ("rider-no-category","183","?season=2025,2026"),
   ("harnden-all","128","?category=elite_women"),
   ("brizin-all","4622","?category=junior_men"),
   ("zierl-all","222","?category=junior_women"),
   ("rider-dnf","18","?category=elite_men&status=DNF"),
   ("rider-dns","60","?category=elite_men&status=DNS"),
   ("rider-dsq","183","?category=elite_men&status=DSQ"),
   ("rider-finished","18","?category=elite_men&status=Finished"),
   ("rank-search","","?season=2026&category=elite_men&search=Bruni&sort=rating&comparison=previousRound"),
   ("rank-name","","?season=2026&category=elite_women&sort=fullName&order=asc&comparison=defaultFilters"),
   ("rank-page2","","?category=elite_men&page=2&pageSize=150&comparison=previousRound"),
   ("rank-page3","","?category=elite_men&page=3&pageSize=150&comparison=previousRound"),
   ("rank-page4","","?category=elite_men&page=4&pageSize=150&comparison=previousRound"),
   ("rank-page5","","?category=elite_men&page=5&pageSize=150&comparison=previousRound"),
   ("rank-country","","?season=2025,2026&category=elite_men&nation=FRA&sort=pace&comparison=previousRound"),
   ("rank-dnf","","?season=2026&category=elite_men&status=DNF&sort=consistency"),
   ("rank-ew-all","","?category=elite_women&pageSize=150&comparison=previousRound"),
   ("rank-jm-all","","?category=junior_men&pageSize=150&comparison=previousRound"),
   ("rank-jw-all","","?category=junior_women&pageSize=150&comparison=previousRound")
  };
  var failures=0;
  var crossPath = new Dictionary<string, JsonNode>();
  var selected = args.Contains("--cases") ? args[Array.IndexOf(args,"--cases")+1].Split(',').ToHashSet() : null;
  var repetitions = args.Contains("--repeat") ? int.Parse(args[Array.IndexOf(args,"--repeat")+1]) : 1;
  var count = 0;
  foreach(var (name,id,qs) in cases)
  foreach(var raw in args.Contains("--preagg-only") ? new[]{false} : new[]{false,true})
  for (var iteration=0; iteration<repetitions; iteration++) {
   if (selected is not null && !selected.Contains(name)) continue;
   count++;
   using var handler=new ProbeHandler(raw) {
    FixtureDirectory = args.Contains("--fixtures") ? args[Array.IndexOf(args,"--fixtures")+1] : null,
    Replay = args.Contains("--replay")
   };
   using var http=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(110)};
   var client=new SupabaseRestClient(http,Options.Create(options));
   using var memory=new MemoryCache(new MemoryCacheOptions());
   var domain=new DomainQueryService(client,packageCache:memory
#if !BASELINE
       ,readOptions:Options.Create(new ReadOptimizationOptions { PreparedRiderDetails=args.Contains("--prepared"), TypedRanking=args.Contains("--typed") })
#endif
   );
   var validator=new QueryValidationService();
   var pace=new PaceService(client);
   var metrics=new MetricsService(domain,pace);
   var ranking=new RankingService(validator,domain,metrics,new RankingSingleFlight(),memory);
   var riders=new RidersService(client,validator,domain,metrics,ranking,new AnalyticsService(validator,domain,pace,TimeProvider.System));
   var query=new QueryCollection(QueryHelpers.ParseQuery(qs));
   var timer=Stopwatch.StartNew();
   object result=id==""?await ranking.ListRankingAsync(query,default):await riders.GetOverviewAsync(id,query,default);
   var ms=timer.Elapsed.TotalMilliseconds;
   var node=JsonSerializer.SerializeToNode(result,new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
   if (args.Contains("--cross-path"))
   {
    if (crossPath.TryGetValue(name, out var reference) && !JsonNode.DeepEquals(reference, node))
    {
     failures++;
     Console.WriteLine("CROSS-PATH DIFF " + name);
    }
    else crossPath.TryAdd(name, node);
   }
   var stem=name+(raw?"-raw":"-preagg");
   var file=Path.Combine(dir,stem+".json");
   if(capture)await File.WriteAllTextAsync(file,node.ToJsonString());
   else {
    var before=JsonNode.Parse(await File.ReadAllTextAsync(file));
    if(!JsonNode.DeepEquals(before,node)){
     failures++;
     await File.WriteAllTextAsync(Path.Combine(dir,stem+".after.json"),node.ToJsonString());
     Console.WriteLine("DIFF "+stem);
    }
   }
   Console.WriteLine(JsonSerializer.Serialize(new{name,raw,ms,mode=capture?"baseline":"candidate",calls=handler.Calls}));
  }
  if(failures>0)throw new Exception(failures+" parity differences");
  Console.WriteLine(args.Contains("--cross-path") ? $"CROSS-PATH PASS: {count} complete responses identical" : capture?$"BASELINE SAVED: {count} responses":$"PARITY PASS: {count} complete responses identical");
 }
}
