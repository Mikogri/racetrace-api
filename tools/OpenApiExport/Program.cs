using System.Text.Json;
using System.Text.Json.Nodes;
using RaceTrace.Api.Controllers;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Infrastructure.Calculations;

if (args is ["--algorithms", var algorithmsOutput])
{
    await WriteDocument(algorithmsOutput, JsonSerializer.SerializeToNode(AlgorithmCatalog.Current.Manifest)!);
    return;
}
if (args.Length != 1) throw new ArgumentException("Usage: OpenApiExport <openapi.json> | --algorithms <manifest.json>");

// Deliberately do not run the production entry point: no secrets, DB clients or hosted jobs.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = [], EnvironmentName = "OpenApiExport", ApplicationName = typeof(RankingController).Assembly.FullName
});
builder.Configuration.Sources.Clear();
builder.Configuration.AddInMemoryCollection();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddRaceTraceHttp();
builder.Services.AddControllers().AddApplicationPart(typeof(RankingController).Assembly);
await using var app = builder.Build();
app.MapControllers();
app.MapOpenApi();
await app.StartAsync();
try
{
    using var client = new HttpClient();
    var document = JsonNode.Parse(await client.GetStringAsync($"{app.Urls.Single()}/openapi/v1.json"))!.AsObject();
    document.Remove("servers"); // Ephemeral export port is not part of the contract.
    document["info"]!["title"] = "RaceTrace API";
    var output = Path.GetFullPath(args[0]);
    await WriteDocument(output, document);
    Console.WriteLine($"Exported {document["paths"]!.AsObject().Count} paths to {output}");
}
finally { await app.StopAsync(); }

static async Task WriteDocument(string path, JsonNode document)
{
    var output = Path.GetFullPath(path);
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    await File.WriteAllTextAsync(output, Canonical(document)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
}

static JsonNode? Canonical(JsonNode? value) => value switch
{
    JsonObject obj => new JsonObject(obj.OrderBy(property => property.Key, StringComparer.Ordinal)
        .Select(property => KeyValuePair.Create(property.Key, Canonical(property.Value)))),
    JsonArray array => new JsonArray(array.Select(Canonical).ToArray()),
    _ => value?.DeepClone()
};
