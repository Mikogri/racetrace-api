using System.Text.Json.Nodes;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Models;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Tests;

public sealed class RankingDataRequestTests
{
    [Fact]
    public async Task ExactRiderIdIsSentSeparatelyFromTextSearch()
    {
        var supabase = new CapturingSupabaseClient();
        var service = new DomainQueryService(supabase);
        var context = new RiderMetricsContext(
            null, null, null, null, null, null, null, null, null, null, null);

        await service.GetRankingDataAsync(context, riderId: "9");

        Assert.Equal("get_ranking_data", supabase.FunctionName);
        var payload = Assert.IsType<JsonObject>(supabase.Payload);
        Assert.Equal(9, payload.Int("p_rider_id"));
        Assert.Null(payload["p_rider_query"]);
    }

    private sealed class CapturingSupabaseClient : ISupabaseRestClient
    {
        public string? FunctionName { get; private set; }
        public JsonObject? Payload { get; private set; }

        public Task<JsonNode?> RpcAsync(
            string functionName,
            JsonObject payload,
            SupabaseRole role,
            CancellationToken cancellationToken = default)
        {
            FunctionName = functionName;
            Payload = payload;
            return Task.FromResult<JsonNode?>(new JsonObject
            {
                ["v"] = 1,
                ["s"] = new JsonArray(),
                ["r"] = new JsonArray(),
                ["f"] = new JsonArray(),
                ["x"] = new JsonArray(),
                ["d"] = new JsonArray(),
                ["t"] = new JsonArray()
            });
        }

        public Task<SupabaseQueryResult> GetAsync(
            SupabaseQuery query,
            bool count = false,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<JsonObject?> GetSingleAsync(
            SupabaseQuery query,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<JsonObject>> PostAsync(
            string table,
            JsonNode payload,
            SupabaseRole role,
            string? onConflict = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<JsonObject>> PatchAsync(
            string table,
            JsonObject payload,
            SupabaseQuery query,
            SupabaseRole role,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteAsync(
            string table,
            SupabaseQuery query,
            SupabaseRole role,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
