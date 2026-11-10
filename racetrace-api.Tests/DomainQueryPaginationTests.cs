using System.Globalization;
using System.Text.Json.Nodes;
using RaceTrace.Api.Features.Results;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class DomainQueryPaginationTests
{
    [Fact]
    public async Task MatchingResultsIncludesFinalWinnerFromSecondSupabasePage()
    {
        var supabase = new PaginatedResultsSupabaseClient();
        var service = new DomainQueryService(supabase);
        var context = new RiderMetricsContext(
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null
        );

        var results = await service.GetMatchingResultsAsync(context);

        Assert.Equal(1001, results.Count);
        Assert.Equal(2, supabase.ResultsQueries.Count);
        Assert.Equal([0, 1000], supabase.ResultsQueries.Select(QueryOffset));

        var winner = Assert.Single(results, result => result.Int("position") == 1);
        Assert.Equal("1001", winner.String("id"));
        Assert.True(ResultEmbedMapping.IsFinalResult(winner));
    }

    private static int QueryOffset(SupabaseQuery query)
    {
        var value = query.Parameters.Last(parameter => parameter.Key == "offset").Value;
        return int.Parse(value, CultureInfo.InvariantCulture);
    }

    private sealed class PaginatedResultsSupabaseClient : ISupabaseRestClient
    {
        public List<SupabaseQuery> ResultsQueries { get; } = [];

        public Task<SupabaseQueryResult> GetAsync(
            SupabaseQuery query,
            bool count = false,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default
        )
        {
            if (query.Table == "sessions")
            {
                return Task.FromResult(
                    new SupabaseQueryResult([new JsonObject { ["id"] = "session-final" }], 1)
                );
            }

            if (query.Table != "results")
            {
                throw new NotSupportedException($"Unexpected table '{query.Table}'.");
            }

            ResultsQueries.Add(query);
            var offset = QueryOffset(query);
            if (offset == 0)
            {
                var firstPage = Enumerable
                    .Range(1, 1000)
                    .Select(index => Result(index, position: index + 1))
                    .ToArray();
                return Task.FromResult(new SupabaseQueryResult(firstPage, 1001));
            }

            return Task.FromResult(new SupabaseQueryResult([Result(1001, position: 1)], 1001));
        }

        private static JsonObject Result(int id, int position)
        {
            return new JsonObject
            {
                ["id"] = id.ToString(CultureInfo.InvariantCulture),
                ["session_id"] = "session-final",
                ["rider_id"] = id.ToString(CultureInfo.InvariantCulture),
                ["position"] = position,
                ["is_classified"] = true,
                ["sessions"] = new JsonObject
                {
                    ["id"] = "session-final",
                    ["session_type"] = "FINAL",
                },
            };
        }

        public Task<JsonObject?> GetSingleAsync(
            SupabaseQuery query,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<JsonObject>> PostAsync(
            string table,
            JsonNode payload,
            SupabaseRole role,
            string? onConflict = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<JsonObject>> PatchAsync(
            string table,
            JsonObject payload,
            SupabaseQuery query,
            SupabaseRole role,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task DeleteAsync(
            string table,
            SupabaseQuery query,
            SupabaseRole role,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<JsonNode?> RpcAsync(
            string functionName,
            JsonObject payload,
            SupabaseRole role,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }
}
