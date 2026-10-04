using System.Text.Json.Nodes;

namespace RaceTrace.Api.Infrastructure;

public sealed record SupabaseQueryResult(IReadOnlyList<JsonObject> Data, int? Total);
