using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class CompetitionClassImportTests
{
    [Fact]
    public async Task ExistingCodeIsMatchedCaseInsensitively()
    {
        var supabase = new CompetitionClassSupabaseClient(
            [new JsonObject { ["id"] = "7", ["code"] = "world_cup" }]);
        var service = new ChronoRaceImportService(supabase, null!, new TestPointAwardService());

        var result = await service.FindOrCreateLegacyCompetitionClassAsync(
            new ImportCompetitionClassPayload("WORLD_CUP", "World Cup", 2),
            CancellationToken.None);

        Assert.Equal("7", result.String("id"));
        Assert.Equal(0, supabase.PostCount);
        Assert.Equal(1, supabase.PatchCount);
        Assert.Equal("world_cup", supabase.WrittenValues?.String("code"));
    }

    [Fact]
    public async Task ConcurrentInsertConflictReloadsAndUpdatesExistingRow()
    {
        var supabase = new CompetitionClassSupabaseClient([]);
        supabase.ConflictOnFirstPost = true;
        var service = new ChronoRaceImportService(supabase, null!, new TestPointAwardService());

        var result = await service.FindOrCreateLegacyCompetitionClassAsync(
            new ImportCompetitionClassPayload("world_cup", "World Cup", 2),
            CancellationToken.None);

        Assert.Equal("8", result.String("id"));
        Assert.Equal(1, supabase.PostCount);
        Assert.Equal(1, supabase.PatchCount);
    }

    private sealed class CompetitionClassSupabaseClient(
        IReadOnlyList<JsonObject> initialRows) : ISupabaseRestClient
    {
        private IReadOnlyList<JsonObject> _rows = initialRows;

        public bool ConflictOnFirstPost { get; set; }

        public int PostCount { get; private set; }

        public int PatchCount { get; private set; }

        public JsonObject? WrittenValues { get; private set; }

        public Task<SupabaseQueryResult> GetAsync(
            SupabaseQuery query,
            bool count = false,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal("competition_ranks", query.Table);
            return Task.FromResult(new SupabaseQueryResult(_rows, _rows.Count));
        }

        public Task<JsonObject?> GetSingleAsync(
            SupabaseQuery query,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<JsonObject>> PostAsync(
            string table,
            JsonNode payload,
            SupabaseRole role,
            string? onConflict = null,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal("competition_ranks", table);
            PostCount++;
            WrittenValues = payload.DeepClone().AsObject();
            if (ConflictOnFirstPost)
            {
                ConflictOnFirstPost = false;
                _rows = [new JsonObject { ["id"] = "8", ["code"] = "WORLD_CUP" }];
                throw new SupabaseApiException(
                    StatusCodes.Status409Conflict,
                    """{"code":"23505"}""");
            }

            return Task.FromResult<IReadOnlyList<JsonObject>>(
                [WithId(WrittenValues, "8")]);
        }

        public Task<IReadOnlyList<JsonObject>> PatchAsync(
            string table,
            JsonObject payload,
            SupabaseQuery query,
            SupabaseRole role,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal("competition_ranks", table);
            PatchCount++;
            WrittenValues = payload.DeepClone().AsObject();
            return Task.FromResult<IReadOnlyList<JsonObject>>(
                [WithId(WrittenValues, _rows[0].String("id")!)]);
        }

        public Task DeleteAsync(
            string table,
            SupabaseQuery query,
            SupabaseRole role,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<JsonNode?> RpcAsync(
            string functionName,
            JsonObject payload,
            SupabaseRole role,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private static JsonObject WithId(JsonObject values, string id)
        {
            var result = values.DeepClone().AsObject();
            result["id"] = id;
            return result;
        }
    }
}
