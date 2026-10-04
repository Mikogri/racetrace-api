using System.Text.Json.Nodes;

namespace RaceTrace.Api.Models;

public sealed record RankingDataBundle(
    IReadOnlyList<JsonObject> Sessions,
    IReadOnlyList<JsonObject> Results,
    IReadOnlyList<JsonObject> FieldResults,
    IReadOnlyList<JsonObject> Splits,
    IReadOnlyDictionary<string, decimal> PointsByResultId);
