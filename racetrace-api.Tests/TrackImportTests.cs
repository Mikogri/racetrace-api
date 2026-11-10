using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class TrackImportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrackNameIsTrimmedAndWrittenForInsertAndUpdate(bool trackExists)
    {
        var supabase = new TrackSupabaseClient(trackExists);
        var service = new ChronoRaceImportService(supabase, null!, new TestPointAwardService());
        var payload = new ImportTrackPayload(
            Name: "  Speedster DH  ",
            Venue: "Leogang",
            CountryCode: "AUT",
            LengthM: 2400,
            AvgSlope: 18.5m
        );

        await service.FindOrCreateTrackAsync(payload, CancellationToken.None);

        Assert.NotNull(supabase.WrittenTrack);
        Assert.Equal("Speedster DH", supabase.WrittenTrack!["name"]!.GetValue<string>());
        Assert.Equal("Leogang", supabase.WrittenTrack["venue"]!.GetValue<string>());
        Assert.Contains(
            supabase.LookupQuery!.Parameters,
            parameter =>
                parameter.Key == "name"
                && parameter.Value.Contains("Speedster DH", StringComparison.Ordinal)
        );
        Assert.DoesNotContain(
            supabase.LookupQuery.Parameters,
            parameter => parameter.Key is "venue" or "country_code"
        );
    }

    [Fact]
    public async Task BlankTrackNameIsRejectedBeforeDatabaseRequest()
    {
        var supabase = new TrackSupabaseClient(trackExists: false);
        var service = new ChronoRaceImportService(supabase, null!, new TestPointAwardService());
        var payload = new ImportTrackPayload(
            Name: "   ",
            Venue: "Leogang",
            CountryCode: "AUT",
            LengthM: null,
            AvgSlope: null
        );

        var exception = await Assert.ThrowsAsync<ApiException>(() =>
            service.FindOrCreateTrackAsync(payload, CancellationToken.None)
        );

        Assert.Equal("TRACK_NAME_REQUIRED", exception.Code);
        Assert.Equal(0, supabase.RequestCount);
    }

    [Fact]
    public async Task MissingOptionalMetadataDoesNotEraseExistingTrackValues()
    {
        var supabase = new TrackSupabaseClient(trackExists: true);
        var service = new ChronoRaceImportService(supabase, null!, new TestPointAwardService());
        var payload = new ImportTrackPayload(
            Name: "Speedster DH",
            Venue: "Leogang",
            CountryCode: null,
            LengthM: null,
            AvgSlope: null
        );

        await service.FindOrCreateTrackAsync(payload, CancellationToken.None);

        Assert.NotNull(supabase.WrittenTrack);
        Assert.False(supabase.WrittenTrack!.ContainsKey("country_code"));
        Assert.False(supabase.WrittenTrack.ContainsKey("length_m"));
        Assert.False(supabase.WrittenTrack.ContainsKey("avg_slope"));
    }

    private sealed class TrackSupabaseClient(bool trackExists) : ISupabaseRestClient
    {
        public int RequestCount { get; private set; }

        public JsonObject? WrittenTrack { get; private set; }

        public SupabaseQuery? LookupQuery { get; private set; }

        public Task<SupabaseQueryResult> GetAsync(
            SupabaseQuery query,
            bool count = false,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default
        )
        {
            throw new NotSupportedException();
        }

        public Task<JsonObject?> GetSingleAsync(
            SupabaseQuery query,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default
        )
        {
            RequestCount++;
            Assert.Equal("tracks", query.Table);
            LookupQuery = query;
            return Task.FromResult<JsonObject?>(
                trackExists ? new JsonObject { ["id"] = "track-1" } : null
            );
        }

        public Task<IReadOnlyList<JsonObject>> PostAsync(
            string table,
            JsonNode payload,
            SupabaseRole role,
            string? onConflict = null,
            CancellationToken cancellationToken = default
        )
        {
            RequestCount++;
            Assert.Equal("tracks", table);
            WrittenTrack = payload.DeepClone().AsObject();
            return Task.FromResult<IReadOnlyList<JsonObject>>([TrackResponse(WrittenTrack)]);
        }

        public Task<IReadOnlyList<JsonObject>> PatchAsync(
            string table,
            JsonObject payload,
            SupabaseQuery query,
            SupabaseRole role,
            CancellationToken cancellationToken = default
        )
        {
            RequestCount++;
            Assert.Equal("tracks", table);
            WrittenTrack = payload.DeepClone().AsObject();
            return Task.FromResult<IReadOnlyList<JsonObject>>([TrackResponse(WrittenTrack)]);
        }

        public Task DeleteAsync(
            string table,
            SupabaseQuery query,
            SupabaseRole role,
            CancellationToken cancellationToken = default
        )
        {
            throw new NotSupportedException();
        }

        public Task<JsonNode?> RpcAsync(
            string functionName,
            JsonObject payload,
            SupabaseRole role,
            CancellationToken cancellationToken = default
        )
        {
            throw new NotSupportedException();
        }

        private static JsonObject TrackResponse(JsonObject payload)
        {
            return new JsonObject
            {
                ["id"] = "track-1",
                ["name"] = payload["name"]!.GetValue<string>(),
                ["venue"] = payload["venue"]!.GetValue<string>(),
            };
        }
    }
}
