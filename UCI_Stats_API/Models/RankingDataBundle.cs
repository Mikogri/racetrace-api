using System.Text.Json.Nodes;

namespace UCI_Stats_API.Models;

public sealed record RankingDataBundle(
    IReadOnlyList<JsonObject> Sessions,
    IReadOnlyList<JsonObject> Results,
    IReadOnlyList<JsonObject> FieldResults,
    IReadOnlyList<JsonObject> Splits,
    IReadOnlyDictionary<string, decimal> PointsByResultId);
