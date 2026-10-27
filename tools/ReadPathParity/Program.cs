using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using RaceTrace.Api.Configuration;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Services;

var api = args.Contains("--api-path") ? args[Array.IndexOf(args,"--api-path")+1] : Path.GetFullPath("racetrace-api");
var config = new ConfigurationBuilder().SetBasePath(api).AddJsonFile("appsettings.json").AddJsonFile("appsettings.Development.json", true)
    .AddJsonFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Microsoft","UserSecrets","racetrace-api-2026","secrets.json"), true).Build();
var options = config.GetSection("Supabase").Get<SupabaseOptions>()!;
if (args.Contains("--incident-cache-benchmark") || args.Contains("--prepare-incidents"))
{
    await IncidentCacheComparison.Run(options, args);
    return;
}
#if !BASELINE
if(args.Contains("--prepare")) {
 using var preparationHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(110) };
 var preparationClient = new SupabaseRestClient(preparationHttp,Options.Create(options));
 var rows=await preparationClient.GetAsync(new SupabaseQuery("ranking_session_preaggregates").Select("session_id,prepared_version"),role:SupabaseRole.Service);
 var service=new RankingPreaggregationService(preparationClient);
 foreach(var row in rows.Data.Where(r=>r.String("prepared_version")!=RankingPreaggregationService.AlgorithmVersion))
  Console.WriteLine($"Prepared {row.String("session_id")}: {await service.PrepareAsync(row.String("session_id")!,default)}");
 return;
}
#endif
if(args.Contains("--parity")) { await Parity.Run(options,args); return; }
if(args.Contains("--stages")) { await Stages.Run(options); return; }
var output = new List<object>();
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var cases = args.Contains("--extra") ? new[] {
 ("asa-2026", "/api/v1/riders/183/overview", "?season=2026&category=elite_men"),
 ("jordan-all", "/api/v1/riders/60/overview", "?category=elite_men")
} : new[] {
 ("ranking-2026", "/api/v1/ranking", "?season=2026&category=elite_men&sort=rating&order=desc&pageSize=30&comparison=previousRound"),
 ("rider-2026", "/api/v1/riders/18/overview", "?season=2026&category=elite_men"),
 ("ranking-all", "/api/v1/ranking", "?category=elite_men&sort=rating&order=desc&pageSize=30&comparison=previousRound"),
 ("rider-all", "/api/v1/riders/18/overview", "?category=elite_men")
};
foreach (var (name,path,qs) in cases)
foreach (var raw in args.Contains("--extra") ? new[] {false} : new[] {false,true})
{
    using var handler = new ProbeHandler(raw);
    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(100) };
    var client = new SupabaseRestClient(http, Options.Create(options));
    for (var iteration = 1; iteration <= 3; iteration++)
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var domain = new DomainQueryService(client, packageCache: memory);
        var validator = new QueryValidationService();
        var pace = new PaceService(client);
        var metrics = new MetricsService(domain,pace);
        var ranking = new RankingService(validator, domain, metrics,new RankingSingleFlight(),memory);
        var rider = new RidersService(client,validator,domain,metrics,ranking,new AnalyticsService(validator,domain,pace,TimeProvider.System));
        var query = new QueryCollection(QueryHelpers.ParseQuery(qs));
        async Task<object> Request() => path.Contains("/riders/")
            ? await rider.GetOverviewAsync(path.Split('/')[4],query,CancellationToken.None)
            : await ranking.ListRankingAsync(query,CancellationToken.None);
        async Task Measure(string mode)
        {
            handler.Calls.Clear();
            var watch=Stopwatch.StartNew();
            try {
                var response=await Request();
                var serviceMs=watch.Elapsed.TotalMilliseconds;
                var serialize=Stopwatch.StartNew();
                var bytes=JsonSerializer.SerializeToUtf8Bytes(response,jsonOptions);
                serialize.Stop();
                var row=new { name, raw, iteration, mode, serviceMs, serializeMs=serialize.Elapsed.TotalMilliseconds,
                    responseBytes=bytes.Length, hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
                    calls=handler.Calls.ToArray() };
                output.Add(row);
                Console.WriteLine(JsonSerializer.Serialize(row));
            } catch(Exception e) {
                var row=new {name,raw,iteration,mode,error=e.GetType().Name,message=e.Message,ms=watch.Elapsed.TotalMilliseconds,calls=handler.Calls.ToArray()};
                output.Add(row);Console.WriteLine(JsonSerializer.Serialize(row));
            }
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"measurements.json"),JsonSerializer.Serialize(output,new JsonSerializerOptions{WriteIndented=true}));
        }
        handler.Replay=false;
        await Measure("cold-api");
        await Measure("warm-api");
        memory.Compact(1);
        handler.Replay=true;
        await Measure("db-replay");
    }
}
// Actual local HTTP response path including output cache; no cache invalidation.
if (args.Contains("--service-only")) return;
using var web=new HttpClient(){Timeout=TimeSpan.FromSeconds(110)};
web.DefaultRequestHeaders.Authorization=new("Bearer",config["ApiAccess:ReadSecret"]);
foreach(var (name,path,qs) in cases)
{
    var url="http://127.0.0.1:5017"+path+qs+"&probe="+Guid.NewGuid().ToString("N");
    for(var run=1;run<=3;run++){
        var watch=Stopwatch.StartNew();
        using var response=await web.GetAsync(url,HttpCompletionOption.ResponseHeadersRead);
        var headersMs=watch.Elapsed.TotalMilliseconds;
        var bytes=await response.Content.ReadAsByteArrayAsync();
        Console.WriteLine(JsonSerializer.Serialize(new{http=name,run,status=(int)response.StatusCode,headersMs,totalMs=watch.Elapsed.TotalMilliseconds,bytes=bytes.Length}));
    }
}

sealed class ProbeHandler(bool raw) : DelegatingHandler(new SocketsHttpHandler{
    AutomaticDecompression=DecompressionMethods.GZip|DecompressionMethods.Deflate|DecompressionMethods.Brotli})
{
    public bool Replay {get;set;}
    public string? FixtureDirectory { get; init; }
    public List<object> Calls {get;}=[];
    readonly Dictionary<string,(byte[] body,HttpStatusCode status,string? range)> recordings=[];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        var uri=request.RequestUri!;
        var endpoint=uri.AbsolutePath.Split('/').Last();
        // Only allow GET or known read-only RPC calls.
        if(request.Method!=HttpMethod.Get && !uri.AbsolutePath.Contains("/rpc/get_"))
            throw new InvalidOperationException("Blocked non-read request: "+uri.AbsolutePath);
        var payload=request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var key=uri.PathAndQuery+"|"+payload;
        var fixture = FixtureDirectory is null ? null : Path.Combine(FixtureDirectory,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key)))+".json");
        var watch=Stopwatch.StartNew();
        if(raw && endpoint is "get_ranking_input_packages" or "get_ranking_metric_packages" or "get_ranking_preaggregate_packages" or "get_ranking_points_summary" or "get_prepared_rider_detail_data"){
            var content=endpoint is "get_ranking_points_summary" or "get_prepared_rider_detail_data" ? "{\"complete\":false,\"rows\":[]}" : "[]";
            Calls.Add(new{endpoint,simulatedCacheMiss=true});
            return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(content,Encoding.UTF8,"application/json")};
        }
        if (raw && endpoint == "get_rider_input_data")
        {
            var input = JsonNode.Parse(payload)!.AsObject();
            input["p_use_preaggregation"] = false;
            payload = input.ToJsonString();
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            key = uri.PathAndQuery + "|" + payload;
        }
        if(Replay) {
            if (fixture is not null) {
                var stored = JsonNode.Parse(await File.ReadAllTextAsync(fixture,ct))!.AsObject();
                recordings[key] = (Convert.FromBase64String(stored["body"]!.GetValue<string>()),
                    (HttpStatusCode)stored["status"]!.GetValue<int>(),stored["range"]?.GetValue<string>());
            }
            if(!recordings.TryGetValue(key,out var cached))throw new InvalidOperationException("Replay miss "+uri.PathAndQuery);
            var copy=new HttpResponseMessage(cached.status){Content=new ByteArrayContent(cached.body)};
            if(cached.range is not null)copy.Content.Headers.TryAddWithoutValidation("Content-Range",cached.range);
            Calls.Add(new{endpoint,replay=true,bytes=cached.body.Length});
            return copy;
        }
        using var response=await base.SendAsync(request,ct);
        var headersMs=watch.Elapsed.TotalMilliseconds;
        var bytes=await response.Content.ReadAsByteArrayAsync(ct);
        var totalMs=watch.Elapsed.TotalMilliseconds;
        var range=response.Content.Headers.TryGetValues("Content-Range",out var ranges)?ranges.FirstOrDefault():null;
        recordings[key]=(bytes,response.StatusCode,range);
        if (fixture is not null && response.IsSuccessStatusCode) {
            Directory.CreateDirectory(FixtureDirectory!);
            var stored = new JsonObject { ["body"] = Convert.ToBase64String(bytes), ["status"] = (int)response.StatusCode, ["range"] = range };
            if (File.Exists(fixture)) {
                var previous = JsonNode.Parse(await File.ReadAllTextAsync(fixture,ct))!;
                if (!JsonNode.DeepEquals(JsonNode.Parse(Convert.FromBase64String(previous["body"]!.GetValue<string>())), JsonNode.Parse(bytes)))
                    throw new InvalidOperationException("Source data changed during capture: "+endpoint);
            } else await File.WriteAllTextAsync(fixture,stored.ToJsonString(),ct);
        }
        var counts=new Dictionary<string,int>();
        try {
            var root=JsonNode.Parse(bytes);
            if(root is JsonObject obj)foreach(var prop in obj)if(prop.Value is JsonArray a)counts[prop.Key]=a.Count;
            if(root is JsonArray rows){
                counts["rows"]=rows.Count;
                foreach(var row in rows.OfType<JsonObject>())
                    if(row["payload"] is JsonObject p)foreach(var prop in p)if(prop.Value is JsonArray a)counts[prop.Key]=counts.GetValueOrDefault(prop.Key)+a.Count;
            }
        }catch(JsonException){}
        lock(Calls)Calls.Add(new{endpoint,status=(int)response.StatusCode,headersMs,totalMs,bodyMs=totalMs-headersMs,bytes=bytes.Length,counts,payload});
        var result=new HttpResponseMessage(response.StatusCode){Content=new ByteArrayContent(bytes)};
        if(range is not null)result.Content.Headers.TryAddWithoutValidation("Content-Range",range);
        return result;
    }
}
