using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class CommonSessionsServiceTests
{
    [Fact]
    public async Task ReturnsOnlySessionsContainingEveryRiderAndIncludesDnfParticipation()
    {
        var supabase = new CommonSessionsSupabaseClient();
        var service = new SessionsService(
            supabase,
            new QueryValidationService(),
            new DomainQueryService(supabase));
        var query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["riderId"] = new StringValues(["3", "8"]),
            ["commonSessionsOnly"] = "true"
        });

        var response = await service.ListCommonSessionsAsync(query, CancellationToken.None);

        var item = Assert.Single(response.Items);
        Assert.Equal(11, item.SessionId);
        Assert.Equal([3L, 8L], item.RiderIds);
    }

    [Fact]
    public async Task CommonSessionsOnlyDoesNotFilterWhenOnlyOneRiderIsProvided()
    {
        var supabase = new SingleRiderSessionsSupabaseClient();
        var service = new SessionsService(
            supabase,
            new QueryValidationService(),
            new DomainQueryService(supabase));
        var query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["riderId"] = "3",
            ["commonSessionsOnly"] = "true"
        });

        var response = await service.ListCommonSessionsAsync(query, CancellationToken.None);

        Assert.Equal(2, response.Items.Count);
    }

    private sealed class CommonSessionsSupabaseClient : ISupabaseRestClient
    {
        private int _sessionQueryCount;

        public Task<SupabaseQueryResult> GetAsync(
            SupabaseQuery query,
            bool count = false,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default)
        {
            if (query.Table == "sessions" && _sessionQueryCount++ == 0)
            {
                IReadOnlyList<JsonObject> sessions =
                [
                    Session("11", "1"),
                    Session("12", "1")
                ];
                return Task.FromResult(new SupabaseQueryResult(sessions, sessions.Count));
            }

            if (query.Table == "results")
            {
                IReadOnlyList<JsonObject> results =
                [
                    new JsonObject { ["session_id"] = "11", ["rider_id"] = "3", ["status"] = "Finished" },
                    new JsonObject { ["session_id"] = "11", ["rider_id"] = "8", ["status"] = "DNF" },
                    new JsonObject { ["session_id"] = "12", ["rider_id"] = "3", ["status"] = "Finished" }
                ];
                return Task.FromResult(new SupabaseQueryResult(results, results.Count));
            }

            if (query.Table == "sessions")
            {
                IReadOnlyList<JsonObject> sessions = [FullSession("11", "1")];
                return Task.FromResult(new SupabaseQueryResult(sessions, sessions.Count));
            }

            throw new NotSupportedException($"Unexpected table '{query.Table}'.");
        }

        private static JsonObject Session(string id, string roundId) => new()
        {
            ["id"] = id,
            ["round_id"] = roundId,
            ["category_id"] = "4",
            ["session_type"] = "FINAL",
            ["session_date"] = "2026-07-20"
        };

        internal static JsonObject FullSession(string id, string roundId) => new()
        {
            ["id"] = id,
            ["round_id"] = roundId,
            ["category_id"] = "4",
            ["session_type"] = "FINAL",
            ["session_date"] = "2026-07-20",
            ["round_categories"] = new JsonObject
            {
                ["rounds"] = new JsonObject { ["id"] = roundId },
                ["categories"] = new JsonObject { ["id"] = "4", ["code"] = "EM" }
            }
        };

        public Task<JsonObject?> GetSingleAsync(SupabaseQuery query, SupabaseRole role = SupabaseRole.Anon, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JsonObject>> PostAsync(string table, JsonNode payload, SupabaseRole role, string? onConflict = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JsonObject>> PatchAsync(string table, JsonObject payload, SupabaseQuery query, SupabaseRole role, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string table, SupabaseQuery query, SupabaseRole role, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JsonNode?> RpcAsync(string functionName, JsonObject payload, SupabaseRole role, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SingleRiderSessionsSupabaseClient : ISupabaseRestClient
    {
        public Task<SupabaseQueryResult> GetAsync(
            SupabaseQuery query,
            bool count = false,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<JsonObject> sessions =
            [
                CommonSessionsSupabaseClient.FullSession("11", "1"),
                CommonSessionsSupabaseClient.FullSession("12", "1")
            ];
            return Task.FromResult(new SupabaseQueryResult(sessions, sessions.Count));
        }

        public Task<JsonObject?> GetSingleAsync(SupabaseQuery query, SupabaseRole role = SupabaseRole.Anon, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JsonObject>> PostAsync(string table, JsonNode payload, SupabaseRole role, string? onConflict = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JsonObject>> PatchAsync(string table, JsonObject payload, SupabaseQuery query, SupabaseRole role, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string table, SupabaseQuery query, SupabaseRole role, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JsonNode?> RpcAsync(string functionName, JsonObject payload, SupabaseRole role, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
