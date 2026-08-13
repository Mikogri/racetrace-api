using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Tests;

public sealed class RiderProfileImageImportTests
{
    private const string FrontUrl = "https://assets.ucimtbworldseries.com/content/78108/conversions/10034793173_NEWKIRK_ANNA_F_CHRONO-newMediumThumb.png";

    [Fact]
    public async Task ImportsFrontImageByUciIdExtractedFromUrl()
    {
        var supabase = new ProfileImageSupabaseClient();
        var service = new RiderProfileImageImportService(supabase);
        var file = JsonFile(new JsonObject
        {
            ["results"] = new JsonArray
            {
                new JsonObject
                {
                    ["riderName"] = "Anna Newkirk",
                    ["rider"] = new JsonObject
                    {
                        ["id"] = 25192,
                        ["uciid"] = null,
                        ["promoImageFront"] = "https://assets.ucimtbworldseries.com/content/78108/conversions/10034793173_NEWKIRK_ANNA_F_CHRONO-newLargeThumb.png",
                        ["worldChampionPromoImageFront"] = FrontUrl
                    }
                }
            }
        });

        var response = await service.ImportAsync([file], CancellationToken.None);

        Assert.Equal(1, response.FilesProcessed);
        Assert.Equal(1, response.ImagesFound);
        Assert.Equal(1, response.RidersUpdated);
        Assert.Equal(0, response.UnmatchedRiders);
        Assert.Equal(FrontUrl, supabase.Patch!["profile_image_url"]!.GetValue<string>());
        Assert.NotNull(supabase.Patch["profile_image_updated_at"]);
        Assert.DoesNotContain("image_data", supabase.Patch.Select(item => item.Key));
    }

    [Fact]
    public async Task ExistingUrlIsUnchangedAndMissingImageDoesNotClearIt()
    {
        var supabase = new ProfileImageSupabaseClient(existingUrl: FrontUrl);
        var service = new RiderProfileImageImportService(supabase);
        var file = JsonFile(new JsonObject
        {
            ["results"] = new JsonArray
            {
                new JsonObject
                {
                    ["rider"] = new JsonObject
                    {
                        ["uciid"] = "10034793173",
                        ["promoImageFront"] = FrontUrl
                    }
                },
                new JsonObject
                {
                    ["rider"] = new JsonObject
                    {
                        ["uciid"] = "10000000000",
                        ["promoImageFront"] = null
                    }
                }
            }
        });

        var response = await service.ImportAsync([file], CancellationToken.None);

        Assert.Equal(1, response.Unchanged);
        Assert.Equal(0, response.RidersUpdated);
        Assert.Null(supabase.Patch);
    }

    [Theory]
    [InlineData("http://assets.example.com/10034793173_NAME_F_CHRONO-newMediumThumb.png")]
    [InlineData("https://assets.example.com/10034793173_NAME_L_CHRONO-newMediumThumb.png")]
    [InlineData("https://assets.example.com/10034793173_NAME_F_CHRONO-newMediumThumb.jpg")]
    public void RejectsNonHttpsNonFrontOrWrongVariantUrls(string value)
    {
        Assert.False(RiderProfileImageImportService.TryNormalizeFrontImageUrl(value, out _));
    }

    private static IFormFile JsonFile(JsonObject payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "files", "results.json")
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/json"
        };
    }

    private sealed class ProfileImageSupabaseClient(string? existingUrl = null) : ISupabaseRestClient
    {
        public JsonObject? Patch { get; private set; }

        public Task<SupabaseQueryResult> GetAsync(
            SupabaseQuery query,
            bool count = false,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(SupabaseRole.Service, role);
            Assert.Equal("riders", query.Table);
            IReadOnlyList<JsonObject> rows =
            [
                new JsonObject
                {
                    ["id"] = "101",
                    ["uci_id"] = "10034793173",
                    ["profile_image_url"] = existingUrl
                }
            ];
            return Task.FromResult(new SupabaseQueryResult(rows, rows.Count));
        }

        public Task<IReadOnlyList<JsonObject>> PatchAsync(
            string table,
            JsonObject payload,
            SupabaseQuery query,
            SupabaseRole role,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(SupabaseRole.Service, role);
            Assert.Equal("riders", table);
            Patch = payload.DeepClone().AsObject();
            return Task.FromResult<IReadOnlyList<JsonObject>>([]);
        }

        public Task<JsonObject?> GetSingleAsync(SupabaseQuery query, SupabaseRole role = SupabaseRole.Anon, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JsonObject>> PostAsync(string table, JsonNode payload, SupabaseRole role, string? onConflict = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string table, SupabaseQuery query, SupabaseRole role, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JsonNode?> RpcAsync(string functionName, JsonObject payload, SupabaseRole role, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
