using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class RoundImageReadContractTests
{
    [Fact]
    public async Task ReusableImageCopiesStorageMetadataWithoutMutatingSource()
    {
        var original = new JsonObject { ["storage_path"] = "images/existing.webp", ["content_type"] = "image/webp" };
        var client = new ImageClient(original, null, true);
        var image = await new RoundImageQueries(client, null).GetReusableImageAsync(42, default);
        image["round_id"] = "99";
        Assert.Equal("images/existing.webp", image.String("storage_path"));
        Assert.False(original.ContainsKey("round_id"));
        Assert.Single(client.Queries);
    }

    [Fact]
    public async Task ReusableImageSupportsVenueFallbackAndRejectsMissingImages()
    {
        var round = new JsonObject { ["tracks"] = new JsonObject { ["venue_images"] = new JsonObject { ["storage_path"] = "venues/existing.webp" } } };
        var client = new ImageClient(null, round, true);
        Assert.Equal("venues/existing.webp", (await new RoundImageQueries(client, null).GetReusableImageAsync(42, default)).String("storage_path"));
        var error = await Assert.ThrowsAsync<ApiException>(() => new RoundImageQueries(new ImageClient(null, null, true), null).GetReusableImageAsync(42, default));
        Assert.Equal("ROUND_IMAGE_SOURCE_NOT_FOUND", error.Code);
    }

    [Fact]
    public async Task MissingImageUsesExactlyOneVenueFallbackRead()
    {
        var client = new ImageClient(null, null);
        using var cancellation = new CancellationTokenSource();

        Assert.Null(await Service(client).GetRoundImageAsync(42, cancellation.Token));

        Assert.Equal(new[] { "round_images", "rounds" }, client.Queries.Select(query => query.Table));
        Assert.Equal("round_id,image_data,storage_path,content_type,file_name,alt_text", client.Queries[0].Parameters.Single(parameter => parameter.Key == "select").Value);
        Assert.Equal("track_id,tracks(venue_image_id,venue_images(storage_path,content_type,file_name,alt_text))", client.Queries[1].Parameters.Single(parameter => parameter.Key == "select").Value);
        Assert.Equal("eq.42", client.Queries[0].Parameters.Single(parameter => parameter.Key == "round_id").Value);
        Assert.Equal("eq.42", client.Queries[1].Parameters.Single(parameter => parameter.Key == "id").Value);
        Assert.All(client.Tokens, token => Assert.Equal(cancellation.Token, token));
    }

    [Theory]
    [InlineData("not base64", "ROUND_IMAGE_INVALID_DATA", 500)]
    [InlineData("\\xBAD", "ROUND_IMAGE_INVALID_DATA", 500)]
    public async Task InvalidBinaryDataKeepsErrorAndDoesNotFallBack(string data, string code, int status)
    {
        var client = new ImageClient(new JsonObject { ["image_data"] = data }, null);

        var error = await Assert.ThrowsAsync<ApiException>(() => Service(client).GetRoundImageAsync(42, default));

        Assert.Equal(code, error.Code);
        Assert.Equal(status, error.StatusCode);
        Assert.Single(client.Queries);
    }

    [Fact]
    public async Task StorageReferencePrecedesInlineDataAndKeepsMissingConfigurationError()
    {
        var client = new ImageClient(new JsonObject
        {
            ["storage_path"] = "rounds/42.png",
            ["image_data"] = "not base64"
        }, null);

        var error = await Assert.ThrowsAsync<ApiException>(() => Service(client).GetRoundImageAsync(42, default));

        Assert.Equal("SUPABASE_STORAGE_NOT_CONFIGURED", error.Code);
        Assert.Equal(503, error.StatusCode);
        Assert.Single(client.Queries);
    }

    private static RoundsService Service(ImageClient client) =>
        new(client, new QueryValidationService(), new DomainQueryService(client));

    private sealed class ImageClient(JsonObject? image, JsonObject? round, bool service = false) : ISupabaseRestClient
    {
        public List<SupabaseQuery> Queries { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];

        public Task<JsonObject?> GetSingleAsync(SupabaseQuery query, SupabaseRole role = SupabaseRole.Anon, CancellationToken cancellationToken = default)
        {
            Assert.Equal(service ? SupabaseRole.Service : SupabaseRole.Anon, role);
            Queries.Add(query);
            Tokens.Add(cancellationToken);
            return Task.FromResult(query.Table == "round_images" ? image : round);
        }

        public Task<SupabaseQueryResult> GetAsync(SupabaseQuery query, bool count = false, SupabaseRole role = SupabaseRole.Anon, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JsonObject>> PostAsync(string table, JsonNode payload, SupabaseRole role, string? onConflict = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JsonObject>> PatchAsync(string table, JsonObject payload, SupabaseQuery query, SupabaseRole role, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string table, SupabaseQuery query, SupabaseRole role, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JsonNode?> RpcAsync(string functionName, JsonObject payload, SupabaseRole role, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
