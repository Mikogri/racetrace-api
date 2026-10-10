using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RaceTrace.Api.Services;
using RaceTrace.Api.Features.Fantasy;

namespace RaceTrace.Api.Infrastructure.Calculations;

public sealed record AlgorithmDefinition(string Version, object Configuration, string[] Dependencies);

/// <summary>Version labels begin at this release, not invented historical releases.
/// Canonical configuration is retained in the manifest for reproducibility.</summary>
public sealed class AlgorithmCatalog(IReadOnlyDictionary<string, AlgorithmDefinition> definitions)
{
    public static AlgorithmCatalog Current { get; } = new(new Dictionary<string, AlgorithmDefinition>
    {
        ["session-weights"] = new("1.0.0", AlgorithmConfiguration.SessionWeights, []),
        ["class-weights"] = new("1.0.0", new { rule = "max-active-priority / priority" }, []),
        ["incidents"] = new("1.0.0", new { pace = AlgorithmConfiguration.Pace, thresholds = AlgorithmConfiguration.Incidents }, []),
        ["pace"] = new("1.0.0", AlgorithmConfiguration.Pace, ["incidents", "session-weights", "class-weights"]),
        ["consistency"] = new("1.0.1", AlgorithmConfiguration.Consistency, ["pace", "session-weights", "class-weights"]),
        ["result-score"] = new("1.0.0", AlgorithmConfiguration.ResultScore, ["session-weights", "class-weights"]),
        ["rating"] = new("1.0.0", AlgorithmConfiguration.Rating, ["pace", "consistency", "result-score"]),
        ["podium"] = new(AlgorithmConfiguration.Podium.ModelVersion, AlgorithmConfiguration.Podium, ["rating"]),
        ["fantasy"] = new(FantasyCalibration.Version, new { rules = FantasyRules2026.Rules, model = FantasyModelConfiguration.Current }, ["podium"]),
        ["round-analytics"] = new("1.0.0", new { }, ["pace"]),
        ["rider-analytics"] = new("1.0.0", new { }, ["rating"])
    });

    public IReadOnlyDictionary<string, AlgorithmDefinition> Manifest => definitions;
    public string VersionLabel(string component) => string.Join(";", Closure(component).Select(key => key + "=" + definitions[key].Version));
    public string Fingerprint(string component) => Hash(Closure(component).ToDictionary(key => key,
        key => new { definitions[key].Version, definitions[key].Configuration }));
    public string ConfigurationHash(string component) => Hash(Closure(component).ToDictionary(key => key,
        key => definitions[key].Configuration));

    private IEnumerable<string> Closure(string component)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string name)
        {
            if (!visited.Add(name)) return;
            foreach (var dependency in definitions[name].Dependencies) Visit(dependency);
        }
        Visit(component);
        return visited.Order(StringComparer.Ordinal);
    }

    public static string Hash(object value)
    {
        static JsonNode? Canonical(JsonNode? node) => node switch
        {
            JsonObject obj => new JsonObject(obj.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => KeyValuePair.Create(pair.Key, Canonical(pair.Value)))),
            JsonArray array => new JsonArray(array.Select(Canonical).ToArray()),
            _ => node?.DeepClone()
        };
        var json = Canonical(JsonSerializer.SerializeToNode(value))!.ToJsonString();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
}

internal static class AlgorithmConfiguration
{
    internal static SessionWeightOptions SessionWeights { get; } = new();
    internal static IncidentDetectionOptions Incidents { get; } = new();
    internal static PaceOptions Pace { get; } = new() { SessionWeights = SessionWeights };
    internal static ConsistencyOptions Consistency { get; } = new();
    internal static RatingOptions Rating { get; } = new();
    internal static ResultScoreOptions ResultScore { get; } = new();
    internal static PodiumPredictionOptions Podium { get; } = new();
}
