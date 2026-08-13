using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Tests;

public sealed class TrackFilterTests
{
    [Fact]
    public async Task CountryVenueAndTrackNameResolveToRoundTrackIds()
    {
        var supabase = new TrackFilterSupabaseClient();
        var service = new DomainQueryService(supabase);
        var context = DomainQueryService.MetricsContextFromQuery(
            new QueryCollection(new Dictionary<string, StringValues>
            {
                ["country_code"] = "AUT",
                ["venue"] = "Leogang",
                ["track_name"] = "Speedster DH"
            }),
            new QueryValidationService());

        var roundIds = await service.ResolveRoundIdsAsync(context);

        Assert.Equal("Leogang", context.Venue);
        Assert.Equal("Speedster DH", context.TrackName);
        Assert.Equal(["round-1"], roundIds);
        AssertQueryParameter(supabase.TrackQuery!, "country_code", "AUT");
        AssertQueryParameter(supabase.TrackQuery!, "venue", "Leogang");
        AssertQueryParameter(supabase.TrackQuery!, "name", "Speedster DH");
        AssertQueryParameter(supabase.RoundQuery!, "track_id", "track-1");
    }

    private static void AssertQueryParameter(
        SupabaseQuery query,
        string key,
        string expectedValue)
    {
        Assert.Contains(query.Parameters, parameter =>
            parameter.Key == key &&
            parameter.Value.Contains(expectedValue, StringComparison.Ordinal));
    }

    private sealed class TrackFilterSupabaseClient : ISupabaseRestClient
    {
        public SupabaseQuery? TrackQuery { get; private set; }

        public SupabaseQuery? RoundQuery { get; private set; }

        public Task<SupabaseQueryResult> GetAsync(
            SupabaseQuery query,
            bool count = false,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default)
        {
            if (query.Table == "tracks")
            {
                TrackQuery = query;
                return Task.FromResult(new SupabaseQueryResult(
                    [new JsonObject { ["id"] = "track-1" }],
                    1));
            }

            if (query.Table == "rounds")
            {
                RoundQuery = query;
                return Task.FromResult(new SupabaseQueryResult(
                    [new JsonObject { ["id"] = "round-1" }],
                    1));
            }

            throw new NotSupportedException($"Unexpected table '{query.Table}'.");
        }

        public Task<JsonObject?> GetSingleAsync(
            SupabaseQuery query,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<JsonObject>> PostAsync(
            string table,
            JsonNode payload,
            SupabaseRole role,
            string? onConflict = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<JsonObject>> PatchAsync(
            string table,
            JsonObject payload,
            SupabaseQuery query,
            SupabaseRole role,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task DeleteAsync(
            string table,
            SupabaseQuery query,
            SupabaseRole role,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<JsonNode?> RpcAsync(
            string functionName,
            JsonObject payload,
            SupabaseRole role,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
