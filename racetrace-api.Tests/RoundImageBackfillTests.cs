using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using RaceTrace.Api.Configuration;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class RoundImageBackfillTests
{
    private const string OnePixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

    [Fact]
    public async Task ConvertsLegacyRoundAndVenueImagesToWebp()
    {
        var png = Convert.FromBase64String(OnePixelPng);
        using var handler = new StorageHandler(png);
        using var http = new HttpClient(handler);
        var storage = new RoundImageStorageService(
            http,
            Options.Create(
                new SupabaseOptions
                {
                    Url = "https://storage.example.test",
                    ServiceRoleKey = "test-key",
                }
            )
        );
        var database = new ImageDatabase(png);

        var backfill = new RoundImageBackfillService(database, storage);
        var result = await backfill.ConvertBatchAsync(10, default);
        var repeated = await backfill.ConvertBatchAsync(10, default);

        Assert.Equal(1, result["round_images_converted"]!.GetValue<int>());
        Assert.Equal(1, result["venue_images_converted"]!.GetValue<int>());
        Assert.Equal("image/webp", database.RoundPatch!["content_type"]!.GetValue<string>());
        Assert.Null(database.RoundPatch["image_data"]);
        Assert.EndsWith(".webp", database.RoundPatch["storage_path"]!.GetValue<string>());
        Assert.Equal("round.webp", database.RoundPatch["file_name"]!.GetValue<string>());
        Assert.Equal("image/webp", database.VenuePatch!["content_type"]!.GetValue<string>());
        Assert.StartsWith("venues/3/", database.VenuePatch["storage_path"]!.GetValue<string>());
        Assert.Equal(2, handler.WebpUploads);
        Assert.Equal(0, repeated["round_images_converted"]!.GetValue<int>());
        Assert.Equal(0, repeated["venue_images_converted"]!.GetValue<int>());
    }

    private sealed class StorageHandler(byte[] png) : HttpMessageHandler
    {
        public int WebpUploads { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(png),
                    }
                );
            Assert.Equal("image/webp", request.Content!.Headers.ContentType?.MediaType);
            WebpUploads++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class ImageDatabase(byte[] png) : ISupabaseRestClient
    {
        public JsonObject? RoundPatch { get; private set; }
        public JsonObject? VenuePatch { get; private set; }

        public Task<SupabaseQueryResult> GetAsync(
            SupabaseQuery query,
            bool count = false,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default
        )
        {
            Assert.Equal(SupabaseRole.Service, role);
            var rows = query.Table switch
            {
                "round_images" when RoundPatch is null => new JsonObject[]
                {
                    new()
                    {
                        ["round_id"] = 7,
                        ["image_data"] = "\\x" + Convert.ToHexString(png),
                        ["file_name"] = "round.png",
                    },
                },
                "venue_images" when VenuePatch is null => new JsonObject[]
                {
                    new()
                    {
                        ["id"] = 3,
                        ["storage_path"] = "images/old.png",
                        ["file_name"] = "venue.png",
                    },
                },
                "round_images" or "venue_images" => [],
                _ => throw new InvalidOperationException(query.Table),
            };
            return Task.FromResult(new SupabaseQueryResult(rows, null));
        }

        public Task<IReadOnlyList<JsonObject>> PatchAsync(
            string table,
            JsonObject payload,
            SupabaseQuery query,
            SupabaseRole role,
            CancellationToken cancellationToken = default
        )
        {
            if (table == "round_images")
                RoundPatch = payload;
            else if (table == "venue_images")
                VenuePatch = payload;
            else
                throw new InvalidOperationException(table);
            return Task.FromResult<IReadOnlyList<JsonObject>>([new JsonObject()]);
        }

        public Task<JsonObject?> GetSingleAsync(
            SupabaseQuery query,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<IReadOnlyList<JsonObject>> PostAsync(
            string table,
            JsonNode payload,
            SupabaseRole role,
            string? onConflict = null,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task DeleteAsync(
            string table,
            SupabaseQuery query,
            SupabaseRole role,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<JsonNode?> RpcAsync(
            string functionName,
            JsonObject payload,
            SupabaseRole role,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();
    }
}
