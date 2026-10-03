using System.Collections;
using System.Text.Json.Nodes;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Models;

/// <summary>Read-only result view: scalar values stay separate from shared metadata.</summary>
internal sealed record RankingReadResult(JsonObject Values, JsonObject? Rider, JsonObject? Team, JsonObject? Session,
    bool IncludeRiderMetadata = true)
{
    public string? String(string key) => Values.String(key);
    public int? Int(string key) => Values.Int(key);
    public decimal? Decimal(string key) => Values.Decimal(key);
    public bool? Bool(string key) => Values.Bool(key);
    public JsonObject? Object(string key) => key switch
    { "riders" => Rider, "teams" => Team, "sessions" => Session, _ => Values.Object(key) };

    public static RankingReadResult FromLegacy(JsonObject value) => new(value, value.Object("riders"), value.Object("teams"), value.Object("sessions"));
    public JsonObject ToLegacy()
    {
        var value = Values.DeepClone().AsObject();
        if (IncludeRiderMetadata)
        {
            value["riders"] = Rider?.DeepClone();
            value["teams"] = Team?.DeepClone();
        }
        value["sessions"] = Session?.DeepClone();
        return value;
    }
}

internal sealed record RankingReadData(
    IReadOnlyList<JsonObject> Sessions,
    IReadOnlyList<RankingReadResult> Results,
    IReadOnlyList<RankingReadResult> FieldResults,
    IReadOnlyList<JsonObject> Splits,
    IReadOnlyDictionary<string, decimal> PointsByResultId)
{
    public RankingDataBundle ToLegacy() => new(Sessions, new LegacyResultList(Results), new LegacyResultList(FieldResults), Splits, PointsByResultId);

    // Compatibility consumers materialize only if they actually read result JSON.
    private sealed class LegacyResultList(IReadOnlyList<RankingReadResult> source) : IReadOnlyList<JsonObject>
    {
        private readonly Lazy<JsonObject[]> _rows = new(() => source.Select(r => r.ToLegacy()).ToArray());
        public int Count => source.Count;
        public JsonObject this[int index] => _rows.Value[index];
        public IEnumerator<JsonObject> GetEnumerator() => ((IEnumerable<JsonObject>)_rows.Value).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
