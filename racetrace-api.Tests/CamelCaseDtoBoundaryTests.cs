using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RaceTrace.Api.Features.Fantasy.Import;
using RaceTrace.Api.Features.Metadata;
using RaceTrace.Api.Configuration;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Tests;

public sealed class CamelCaseDtoBoundaryTests
{
    [Fact]
    public void HttpMetricsUseCamelCaseForFormerSnakeCaseOverrides()
    {
        var metrics = new RiderPerformanceMetricValues(90, 89, 88, 87, 0.5m, 4, 86, 0.6m, 3, 8)
        {
            PaceUsedFinals = 2,
            ResultScore = 85
        };

        var json = HttpJson(metrics);

        Assert.Equal(89m, json["rawRating"]!.GetValue<decimal>());
        Assert.Equal(87m, json["rawPace"]!.GetValue<decimal>());
        Assert.Equal(0.6m, json["consistencyEvidence"]!.GetValue<decimal>());
        Assert.Equal(2, json["paceUsedFinals"]!.GetValue<int>());
        Assert.Equal(85m, json["resultScore"]!.GetValue<decimal>());
        Assert.DoesNotContain(json.AsObject(), property => property.Key.Contains('_'));
    }

    [Fact]
    public void SplitAnalyticsDtosKeepOptionalInitPropertiesAndCamelCaseOutput()
    {
        var spread = new FieldSpreadPointDto(2, 120, 1, 0.8m) { PreviousPosition = 1 };
        var profile = new RoundProfileResponseDto(2026, "men_elite", null, 12, [],
            new FieldSpreadDto(2, "close", null, [spread]), new TrackFingerprintDto(null, []));

        var json = HttpJson(profile);

        Assert.Equal(12, json["sessionId"]!.GetValue<int>());
        Assert.Equal(1, json["fieldSpread"]!["points"]![0]!["previousPosition"]!.GetValue<int>());
        Assert.Empty(json["trackFingerprint"]!["positionSpread"]!.AsArray());
        Assert.Null(json["analytics"]);
        Assert.Null(profile.Analytics);
        Assert.Null(spread.IncrementalSpread);
    }

    [Fact]
    public void HttpPaginationAndRequestsUseCamelCaseWithoutChangingOptionalValues()
    {
        var pagination = HttpJson(new PaginationMetadata(1, 20, 60, 3));
        Assert.Equal(3, pagination["totalPages"]!.GetValue<int>());
        Assert.Null(pagination["total_pages"]);

        using var provider = new ServiceCollection().AddLogging().AddRaceTraceHttp().BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>().Value.JsonSerializerOptions;
        var request = JsonSerializer.Deserialize<PointsRecalculateRequest>(
            """{"seasonYear":2026,"categoryCode":"men_elite"}""", options)!;
        Assert.Equal(2026, request.SeasonYear);
        Assert.Equal("men_elite", request.CategoryCode);
        Assert.Null(request.Scope);
        Assert.Null(request.SessionId);
    }

    [Fact]
    public void HttpDictionaryKeysRemainDataRatherThanPropertyNames()
    {
        var chart = new RiderChartStatisticsDto(null,
            new Dictionary<string, decimal> { ["round_12"] = 10 },
            new Dictionary<string, decimal?> { ["Val_di_Sole"] = 2.5m });

        var json = HttpJson(chart);

        Assert.Equal(10m, json["cumulativePointsByRound"]!["round_12"]!.GetValue<decimal>());
        Assert.Equal(2.5m, json["preFinalAverageByTrack"]!["Val_di_Sole"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task DisplayMetadataStillDeserializesItsDatabaseContractWithoutHttpOptions()
    {
        const string payload = """
            {"categories":[],"sessionTypes":[],"roundStatuses":[
              {"code":"planned","name":"Planned","shortName":"Plan","displayOrder":1}],
             "sessionStatuses":[]}
            """;

        using var handler = new MetadataHandler(payload);
        using var http = new HttpClient(handler);
        var service = new DisplayMetadataService(new SupabaseRestClient(http,
            Options.Create(new SupabaseOptions { Url = "https://example.supabase.co", AnonKey = "anon" })));
        var metadata = await service.GetAsync(default);

        Assert.Equal("Plan", Assert.Single(metadata.RoundStatuses).ShortName);
        Assert.Equal(1, metadata.RoundStatuses[0].DisplayOrder);
        Assert.DoesNotContain("databaseCode", JsonSerializer.Serialize(metadata));
    }

    [Fact]
    public void PrivateFantasySnapshotsAndRawProviderJsonKeepTheirOwnSerializerContracts()
    {
        const string stored = """
            {"id":"snapshot_1","season":2026,"upcomingRoundId":"12",
             "fetchedAtUtc":"2026-01-01T00:00:00+00:00","riders":[],
               "sourceUrl":"https://example.test","acquisitionMethod":"http","dataVersion":1}
            """;
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var snapshot = JsonSerializer.Deserialize<FantasyPriceSnapshot>(stored, web)!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(stored), JsonSerializer.SerializeToNode(snapshot, web)));

        var provider = JsonNode.Parse("""{"session_id":12,"raw_rating":80}""")!;
        var roundTrip = JsonSerializer.SerializeToNode(provider, web)!;
        Assert.Equal(12, roundTrip["session_id"]!.GetValue<int>());
        Assert.Equal(80, roundTrip["raw_rating"]!.GetValue<int>());
        Assert.Null(roundTrip["sessionId"]);
    }

    private static JsonNode HttpJson<T>(T value)
    {
        using var provider = new ServiceCollection().AddLogging().AddRaceTraceHttp().BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>().Value.JsonSerializerOptions;
        return JsonSerializer.SerializeToNode(value, options)!;
    }

    private sealed class MetadataHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(payload) });
    }
}
