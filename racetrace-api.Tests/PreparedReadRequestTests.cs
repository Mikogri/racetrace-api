using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using RaceTrace.Api.Configuration;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class PreparedReadRequestTests
{
    [Theory]
    [InlineData("{\"r\":[[1,18,10]],\"a\":[[1,10]],\"pf\":[]}", 200)]
    [InlineData("{\"r\":[[1,18,10]],\"a\":[],\"pf\":[]}", 200)]
    [InlineData(
        "{\"r\":[[1,18,10]],\"a\":[[1,10]],\"pf\":[[10,18,249,1,2,null,5.5,0.02,1,249,269]]}",
        200
    )]
    [InlineData(
        "{\"r\":[[1,18,10]],\"a\":[[1,11]],\"pf\":[[11,18,null,null,null,null,null,null,null,null,null]]}",
        200
    )]
    [InlineData("{}", 404)]
    public async Task IncompletePreparedEnvelopeFallsBack(string payload, int statusCode)
    {
        using var handler = new Handler(true)
        {
            PreparedPayload = payload,
            PreparedStatus = (HttpStatusCode)statusCode,
        };
        using var http = new HttpClient(handler);
        var client = new SupabaseRestClient(
            http,
            Options.Create(new SupabaseOptions { Url = "https://test.invalid", AnonKey = "test" })
        );
        var domain = new DomainQueryService(
            client,
            readOptions: Options.Create(new ReadOptimizationOptions { PreparedRiderDetails = true })
        );
        await domain.GetRiderDetailDataAsync(Filters(), "18");
        Assert.Equal(new[] { "get_rider_input_data", "get_rider_detail_data" }, handler.Calls);
    }

    private static RiderMetricsContext Filters(string? status = null) =>
        new(2026, "EM", null, null, null, null, null, null, null, null, status);

    [Theory]
    [InlineData(false, null, 2)]
    [InlineData(true, null, 1)]
    [InlineData(true, "DNF", 1)]
    public async Task PreparedReadUsesCompatibleDataOrFallsBack(
        bool complete,
        string? status,
        int calls
    )
    {
        using var handler = new Handler(complete);
        using var http = new HttpClient(handler);
        var client = new SupabaseRestClient(
            http,
            Options.Create(new SupabaseOptions { Url = "https://test.invalid", AnonKey = "test" })
        );
        var domain = new DomainQueryService(
            client,
            readOptions: Options.Create(new ReadOptimizationOptions { PreparedRiderDetails = true })
        );
        var result = await domain.GetRiderDetailDataAsync(Filters(status), "18");
        Assert.Empty(result.Data.Results);
        Assert.Equal(calls, handler.Calls.Count);
        Assert.Equal("get_rider_input_data", handler.Calls[0]);
        if (!complete)
            Assert.Equal("get_rider_detail_data", handler.Calls[^1]);
    }

    [Fact]
    public async Task SourceRefreshChecksInputAvailabilityNotCalculatorVersion()
    {
        using var handler = new Handler(true);
        using var http = new HttpClient(handler);
        var client = new SupabaseRestClient(
            http,
            Options.Create(
                new SupabaseOptions { Url = "https://test.invalid", ServiceRoleKey = "test" }
            )
        );
        Assert.True(await new RankingPreaggregationService(client).RefreshAsync("10", default));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("input_splits", handler.Requests[1]);
        Assert.All(handler.Requests, request => Assert.DoesNotContain("prepared_", request));
    }

    [Fact]
    public async Task ChangedRevisionRejectsPreparedSaveWithoutDeletingSource()
    {
        using var handler = new Handler(false);
        using var http = new HttpClient(handler);
        var client = new SupabaseRestClient(
            http,
            Options.Create(
                new SupabaseOptions { Url = "https://test.invalid", ServiceRoleKey = "test" }
            )
        );
        Assert.False(await new RankingPreaggregationService(client).PrepareAsync("10", default));
        Assert.Equal(
            new[] { "ranking_session_preaggregates", "save_prepared_ranking_session" },
            handler.Calls
        );
        Assert.Equal(
            "11111111-1111-1111-1111-111111111111",
            handler.Save!["p_source_revision"]!.GetValue<string>()
        );
    }

    private sealed class Handler(bool complete) : HttpMessageHandler
    {
        public string? PreparedPayload { get; init; }
        public HttpStatusCode PreparedStatus { get; init; } = HttpStatusCode.OK;
        public List<string> Calls { get; } = [];
        public List<string> Requests { get; } = [];
        public JsonObject? Save { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken token
        )
        {
            var name = request.RequestUri!.Segments.Last();
            Calls.Add(name);
            Requests.Add(request.RequestUri.PathAndQuery);
            var body = name switch
            {
                "get_rider_input_data" => complete
                    ? "{\"complete\":true,\"payload\":{\"v\":3,\"r\":[],\"s\":[],\"f\":[],\"ix\":[],\"x\":[]}}"
                    : "{\"complete\":false}",
                "get_rider_detail_data" => "{\"r\":[]}",
                "ranking_session_preaggregates" =>
                    "[{\"source_revision\":\"11111111-1111-1111-1111-111111111111\",\"payload\":{}}]",
                "save_prepared_ranking_session" => "false",
                "get_ranking_input_packages" =>
                    "[{\"payload\":{\"v\":3,\"s\":[],\"r\":[],\"f\":[],\"ix\":[],\"x\":[]}}]",
                "save_ranking_incident_annotations" => "true",
                "refresh_ranking_session_preaggregate" => "null",
                _ => throw new InvalidOperationException(name),
            };
            if (name == "save_prepared_ranking_session")
                Save = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
            if (name == "get_rider_input_data")
                return new HttpResponseMessage(PreparedStatus)
                {
                    Content = new StringContent(
                        PreparedPayload is null
                            ? body
                            : "{\"complete\":true,\"payload\":" + PreparedPayload + "}"
                    ),
                };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
}
