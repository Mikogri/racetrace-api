using System.Text.Json.Nodes;

namespace UCI_Stats_API.Infrastructure;

public sealed record SupabaseQueryResult(IReadOnlyList<JsonObject> Data, int? Total);
