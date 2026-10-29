using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class CompareInputPackageReaderTests
{
    private static RiderMetricsContext Context => new(2026, "ME", null, null, null, null, null, null, null, null, null)
    { CategoryCodes = ["ME"] };

    [Fact]
    public async Task SharedPackageProjectsOnlyEachRidersResultsButKeepsAllMatchingSessions()
    {
        var client = new Client();
        var shared = await new CompareInputPackageReader(client).ReadAsync(Context, ["3", "8"], default);
        Assert.NotNull(shared);
        var first = CompareInputPackageReader.ForRider(shared!, "3");
        var second = CompareInputPackageReader.ForRider(shared!, "8");
        Assert.Equal("3", Assert.Single(first.Data.Results).String("rider_id"));
        Assert.Empty(second.Data.Results);
        Assert.Equal(2, second.Data.Sessions.Count);
        Assert.Same(first.Data.Sessions, second.Data.Sessions);
        Assert.Same(first.Data.FieldResults, second.Data.FieldResults);
        Assert.Equal(7m, first.Data.PointsByResultId["31"]);
        Assert.Empty(second.Data.PointsByResultId);
        Assert.Empty(second.Data.Splits);
        Assert.Equal("get_compare_input_data", client.Function);
        Assert.Equal("[3,8]", client.Payload!["p_rider_ids"]!.ToJsonString());
        Assert.Equal("[2026]", client.Payload["p_season_years"]!.ToJsonString());
        Assert.False(client.Payload["p_use_preaggregation"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(500)]
    [InlineData(404)]
    public async Task PermissionAndServerFailuresDoNotBecomeLegacyReads(int status)
    {
        var client = new Client { Error = new SupabaseApiException(status, "failure") };
        await Assert.ThrowsAsync<SupabaseApiException>(() => new CompareInputPackageReader(client).ReadAsync(Context, ["3", "8"], default));
    }

    [Fact]
    public async Task OnlyUnavailableRpcAllowsLegacyFallback()
    {
        var client = new Client { Error = new SupabaseApiException(404, "{\"code\":\"PGRST202\"}") };
        Assert.Null(await new CompareInputPackageReader(client).ReadAsync(Context, ["3", "8"], default));
    }

    [Fact]
    public async Task MalformedPackageIsReportedInsteadOfSilentlyReadingAgain()
    {
        var client = new Client { Malformed = true };
        var error = await Assert.ThrowsAsync<ApiException>(() => new CompareInputPackageReader(client).ReadAsync(Context, ["3", "8"], default));
        Assert.Equal(502, error.StatusCode);
    }

    private sealed class Client : ISupabaseRestClient
    {
        public Exception? Error { get; init; }
        public bool Malformed { get; init; }
        public string? Function { get; private set; }
        public JsonObject? Payload { get; private set; }
        public Task<JsonNode?> RpcAsync(string functionName, JsonObject payload, SupabaseRole role, CancellationToken cancellationToken = default)
        {
            Function = functionName;
            Payload = payload;
            if (Error is not null) throw Error;
            return Task.FromResult<JsonNode?>(JsonNode.Parse(Malformed ? "{}" : """
                {"complete":true,"cached":true,"payload":{"v":3,
                "s":[[100,1,1,"Final","FINAL",null,1,2026,"2026-01-01",1,"ME"],[200,2,1,"Final","FINAL",null,1,2026,"2026-02-01",1,"ME"]],
                "r":[[31,3,100,100000,null,1,true,"Finished",7,null]],
                "d":[[3,null,"First","Rider","POL"],[8,null,"Second","Rider","POL"]],
                "t":[],"f":[[31,3,100,100000,null,1,true,"Finished"],[99,99,100,101000,null,2,true,"Finished"]],
                "ix":[],"x":[]}}
                """));
        }
        public Task<SupabaseQueryResult> GetAsync(SupabaseQuery query, bool count = false, SupabaseRole role = SupabaseRole.Anon, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JsonObject?> GetSingleAsync(SupabaseQuery query, SupabaseRole role = SupabaseRole.Anon, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JsonObject>> PostAsync(string table, JsonNode payload, SupabaseRole role, string? onConflict = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JsonObject>> PatchAsync(string table, JsonObject payload, SupabaseQuery query, SupabaseRole role, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string table, SupabaseQuery query, SupabaseRole role, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
