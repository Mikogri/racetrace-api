using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using UCI_Stats_API.Controllers;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Tests;

public sealed class RoundsServiceImageQueryTests
{
    [Fact]
    public async Task RoundListAlwaysOrdersByDateAcrossCompetitionClasses()
    {
        var supabase = new RoundImageSupabaseClient();
        var service = new RoundsService(
            supabase,
            new QueryValidationService(),
            new DomainQueryService(supabase));

        await service.ListRoundsAsync(
            new QueryCollection(new Dictionary<string, StringValues>
            {
                ["season"] = "2026",
                ["sort"] = "roundNumber",
                ["order"] = "asc"
            }),
            CancellationToken.None);

        Assert.Contains(
            supabase.RoundsQuery!.Parameters,
            parameter =>
                parameter.Key == "order" &&
                parameter.Value == "start_date.asc.nullslast,id.asc");
    }

    [Fact]
    public async Task RoundListLoadsOnlyImageMetadataInSmallBatches()
    {
        var supabase = new RoundImageSupabaseClient();
        var validator = new QueryValidationService();
        var service = new RoundsService(
            supabase,
            validator,
            new DomainQueryService(supabase));

        var response = await service.ListRoundsAsync(
            SeasonQuery(),
            CancellationToken.None);

        Assert.Equal(3, response.Items.Count);
        Assert.DoesNotContain("round_images", SelectParameter(supabase.RoundsQuery!));
        Assert.Single(supabase.ImageQueries);
        Assert.All(supabase.ImageQueries, query =>
        {
            Assert.Equal("round_id", SelectParameter(query));
            Assert.DoesNotContain("image_data", SelectParameter(query));
            Assert.Contains(query.Parameters, parameter =>
                parameter.Key == "round_id" &&
                parameter.Value.Split(',').Length <= 100);
        });

        Assert.Equal("/api/v1/rounds/1/image", response.Items[0].ImageUrl);
    }

    [Fact]
    public async Task IncludeImageCannotRestoreBinaryDataInRoundList()
    {
        var supabase = new RoundImageSupabaseClient();
        var validator = new QueryValidationService();
        var service = new RoundsService(
            supabase,
            validator,
            new DomainQueryService(supabase));

        var response = await service.ListRoundsAsync(
            new QueryCollection(new Dictionary<string, StringValues>
            {
                ["season"] = "2026",
                ["include_image"] = "true"
            }),
            CancellationToken.None);

        Assert.NotEmpty(supabase.ImageQueries);
        Assert.All(supabase.ImageQueries, query =>
            Assert.DoesNotContain("image_data", SelectParameter(query)));
        Assert.All(response.Items, round => Assert.NotNull(round.ImageUrl));
    }

    [Fact]
    public async Task GetRoundImageDecodesPostgresHexAndReturnsMetadata()
    {
        var supabase = new RoundImageSupabaseClient();
        var service = new RoundsService(
            supabase,
            new QueryValidationService(),
            new DomainQueryService(supabase));

        var image = await service.GetRoundImageAsync(1, CancellationToken.None);

        Assert.NotNull(image);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, image.Bytes);
        Assert.Equal("image/png", image.ContentType);
        Assert.Equal("round.png", image.FileName);
        Assert.Equal("Round map", image.AltText);
    }

    [Fact]
    public async Task RoundImageEndpointReturnsPngWithPublicCacheHeader()
    {
        var supabase = new RoundImageSupabaseClient();
        var validator = new QueryValidationService();
        var controller = new RoundsController(
            new RoundsService(supabase, validator, new DomainQueryService(supabase)),
            validator)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var response = await controller.Image(1, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(response);
        Assert.Equal("image/png", file.ContentType);
        Assert.Equal("public, max-age=86400", controller.Response.Headers.CacheControl);
    }

    private static string SelectParameter(SupabaseQuery query)
    {
        return query.Parameters.Single(parameter => parameter.Key == "select").Value;
    }

    private static QueryCollection SeasonQuery() => new(new Dictionary<string, StringValues>
    {
        ["season"] = "2026"
    });

    private sealed class RoundImageSupabaseClient : ISupabaseRestClient
    {
        public SupabaseQuery? RoundsQuery { get; private set; }

        public List<SupabaseQuery> ImageQueries { get; } = [];

        public Task<SupabaseQueryResult> GetAsync(
            SupabaseQuery query,
            bool count = false,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default)
        {
            if (query.Table == "rounds")
            {
                RoundsQuery = query;
                return Task.FromResult(new SupabaseQueryResult(
                    [Round("1"), Round("2"), Round("3")],
                    3));
            }

            if (query.Table == "round_images")
            {
                ImageQueries.Add(query);
                var ids = new[] { "1", "2", "3" };
                return Task.FromResult(new SupabaseQueryResult(
                    ids.Select(Image).ToArray(),
                    null));
            }

            if (query.Table == "sessions")
            {
                IReadOnlyList<JsonObject> statuses =
                [
                    new JsonObject { ["round_id"] = "1", ["status"] = "completed" },
                    new JsonObject { ["round_id"] = "2", ["status"] = "completed" },
                    new JsonObject { ["round_id"] = "3", ["status"] = "completed" }
                ];
                return Task.FromResult(new SupabaseQueryResult(statuses, null));
            }

            throw new NotSupportedException($"Unexpected table '{query.Table}'.");
        }

        private static JsonObject Round(string id) => new()
        {
            ["id"] = id,
            ["context_name"] = $"round-{id}",
            ["season_year"] = 2026,
            ["track_id"] = id
        };

        private static JsonObject Image(string roundId) => new()
        {
            ["round_id"] = roundId,
            ["image_data"] = "\\x89504E470D0A1A0A",
            ["content_type"] = "image/png",
            ["file_name"] = "round.png",
            ["alt_text"] = "Round map"
        };

        public Task<JsonObject?> GetSingleAsync(
            SupabaseQuery query,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<JsonObject?>(Image("1"));

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

        public Task<JsonNode?> RpcAsync(
            string functionName,
            JsonObject payload,
            SupabaseRole role,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
