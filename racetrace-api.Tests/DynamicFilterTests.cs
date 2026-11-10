using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using RaceTrace.Api.Configuration;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class DynamicFilterTests
{
    [Theory]
    [InlineData("u23_open", "super_final")]
    [InlineData("U23_OPEN", "SUPER_FINAL")]
    [InlineData("Open U23", "Last Run")]
    public async Task RegisteredCodesAndAliasesReachSessionQueries(
        string category,
        string sessionType
    )
    {
        using var handler = new FilterHandler();
        using var http = new HttpClient(handler);
        var client = Client(http);
        var service = new SessionsService(
            client,
            new QueryValidationService(),
            new DomainQueryService(client)
        );

        await service.ListCommonSessionsAsync(Query(category, sessionType), default);

        Assert.Contains(
            handler.Queries,
            query => query.Contains("code=in.(U23_OPEN)", StringComparison.Ordinal)
        );
        Assert.Contains(
            handler.Queries,
            query => query.Contains("session_type=in.(SUPER_FINAL)", StringComparison.Ordinal)
        );
        Assert.Equal(1, handler.MetadataCalls);
    }

    [Fact]
    public async Task ExistingAliasesDoNotLoadMetadata()
    {
        using var handler = new FilterHandler();
        using var http = new HttpClient(handler);
        var client = Client(http);
        var service = new SessionsService(
            client,
            new QueryValidationService(),
            new DomainQueryService(client)
        );

        await service.ListCommonSessionsAsync(Query("Men Elite", "qualifying_1"), default);

        Assert.Contains(
            handler.Queries,
            query => query.Contains("code=in.(EM)", StringComparison.Ordinal)
        );
        Assert.Contains(
            handler.Queries,
            query => query.Contains("session_type=in.(Q1)", StringComparison.Ordinal)
        );
        Assert.Equal(0, handler.MetadataCalls);
    }

    [Theory]
    [InlineData("not_registered", "final")]
    [InlineData("elite_men", "not_registered")]
    public async Task UnregisteredFiltersRemainBadRequests(string category, string sessionType)
    {
        using var handler = new FilterHandler();
        using var http = new HttpClient(handler);
        var client = Client(http);
        var service = new SessionsService(
            client,
            new QueryValidationService(),
            new DomainQueryService(client)
        );

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            service.ListCommonSessionsAsync(Query(category, sessionType), default)
        );

        Assert.Equal(400, error.StatusCode);
        Assert.Contains("Unsupported", error.Message);
        Assert.DoesNotContain(
            handler.Queries,
            query => query.StartsWith("/rest/v1/sessions?", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task MixedFiltersPreserveOrderAndCanonicalDeduplication()
    {
        using var handler = new FilterHandler();
        using var http = new HttpClient(handler);
        var client = Client(http);
        var service = new SessionsService(
            client,
            new QueryValidationService(),
            new DomainQueryService(client)
        );

        await service.ListCommonSessionsAsync(
            Query("u23_open,elite_men,Open U23", "super_final,final,Last Run"),
            default
        );

        Assert.Contains(
            handler.Queries,
            query => query.Contains("code=in.(U23_OPEN,EM)", StringComparison.Ordinal)
        );
        Assert.Contains(
            handler.Queries,
            query => query.Contains("session_type=in.(SUPER_FINAL,FINAL)", StringComparison.Ordinal)
        );
        Assert.Equal(1, handler.MetadataCalls);
    }

    [Fact]
    public async Task RoundSessionFiltersAcceptRegisteredCodes()
    {
        using var handler = new FilterHandler();
        using var http = new HttpClient(handler);
        var client = Client(http);
        var service = new RoundsService(
            client,
            new QueryValidationService(),
            new DomainQueryService(client)
        );

        await service.ListRoundSessionsAsync("4", Query("u23_open", "super_final"), default);

        Assert.Contains(
            handler.Queries,
            query => query.Contains("code=eq.U23_OPEN", StringComparison.Ordinal)
        );
        Assert.Contains(
            handler.Queries,
            query => query.Contains("session_type=eq.SUPER_FINAL", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task SeasonSummaryAcceptsRegisteredCategory()
    {
        using var handler = new FilterHandler();
        using var http = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var client = Client(http);
        var validator = new QueryValidationService();
        var domain = new DomainQueryService(client);
        var metrics = new MetricsService(domain, new PaceService(client));
        var ranking = new RankingService(
            validator,
            domain,
            metrics,
            new RankingSingleFlight(),
            cache
        );
        var service = new SeasonSummaryService(
            client,
            validator,
            domain,
            ranking,
            TimeProvider.System
        );

        var result = await service.GetAsync(Query("Open U23", "final"), default);

        Assert.Equal("u23_open", result.Category);
        Assert.Empty(result.CurrentStandings);
        Assert.Contains(
            handler.Queries,
            query => query.Contains("code=eq.U23_OPEN", StringComparison.Ordinal)
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResultsRoutesAcceptAndValidateRegisteredFilters(bool byRound)
    {
        using var handler = new FilterHandler { ReturnResults = true };
        using var http = new HttpClient(handler);
        var client = Client(http);
        var domain = new DomainQueryService(client);
        var service = new ResultsService(
            client,
            new QueryValidationService(),
            domain,
            new MetricsService(domain, new PaceService(client))
        );

        var result = byRound
            ? await service.ListPublicRoundResultsAsync("4", Query("Open U23", "Last Run"), default)
            : await service.ListPublicSessionResultsAsync(
                "10",
                Query("Open U23", "Last Run"),
                default
            );

        Assert.Equal("u23_open", result.Session.Category);
        Assert.Equal("super_final", result.Session.SessionType);
        Assert.Equal(1, result.TotalItems);
        Assert.Equal(1, handler.MetadataCalls);
        if (byRound)
        {
            Assert.Contains(
                handler.Queries,
                query =>
                    query.Contains("sessions.session_type=eq.SUPER_FINAL", StringComparison.Ordinal)
            );
            Assert.Contains(
                handler.Queries,
                query =>
                    query.Contains(
                        "sessions.round_categories.categories.code=eq.U23_OPEN",
                        StringComparison.Ordinal
                    )
            );
        }
    }

    [Fact]
    public async Task AllRoundResultsPreserveSourceSessionAndDoNotFilterSessionType()
    {
        using var handler = new FilterHandler { ReturnResults = true };
        using var http = new HttpClient(handler);
        var client = Client(http);
        var domain = new DomainQueryService(client);
        var service = new ResultsService(
            client,
            new QueryValidationService(),
            domain,
            new MetricsService(domain, new PaceService(client))
        );
        var result = await service.ListPublicRoundResultsAsync(
            "4",
            Query("Open U23", "all"),
            default
        );
        Assert.Equal("all", result.Session.SessionType);
        Assert.Equal(
            "super_final",
            Assert.Single(result.Items.Where(item => !item.IsPerfectRun)).SessionType
        );
        Assert.DoesNotContain(
            handler.Queries,
            query => query.Contains("sessions.session_type=", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task RegisteredButMismatchedSessionFiltersRemainBadRequests()
    {
        using var handler = new FilterHandler { ReturnResults = true };
        using var http = new HttpClient(handler);
        var client = Client(http);
        var domain = new DomainQueryService(client);
        var service = new ResultsService(
            client,
            new QueryValidationService(),
            domain,
            new MetricsService(domain, new PaceService(client))
        );

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            service.ListPublicSessionResultsAsync("10", Query("Open U23", "final"), default)
        );

        Assert.Equal(400, error.StatusCode);
        Assert.Contains("does not match this session", error.Message);
    }

    [Fact]
    public async Task MetadataOutageIsNotReportedAsAnUnsupportedFilter()
    {
        using var handler = new FilterHandler
        {
            MetadataStatus = HttpStatusCode.ServiceUnavailable,
        };
        using var http = new HttpClient(handler);
        var client = Client(http);
        var service = new SessionsService(
            client,
            new QueryValidationService(),
            new DomainQueryService(client)
        );

        var error = await Assert.ThrowsAsync<SupabaseApiException>(() =>
            service.ListCommonSessionsAsync(Query("u23_open", "final"), default)
        );

        Assert.Equal(503, error.StatusCode);
    }

    private static IQueryCollection Query(string category, string sessionType) =>
        new QueryCollection(
            new Dictionary<string, StringValues>
            {
                ["category"] = category,
                ["sessionType"] = sessionType,
            }
        );

    private static SupabaseRestClient Client(HttpClient http) =>
        new(
            http,
            Options.Create(
                new SupabaseOptions { Url = "https://example.supabase.co", AnonKey = "anon" }
            )
        );

    private sealed class FilterHandler : HttpMessageHandler
    {
        public List<string> Queries { get; } = [];
        public int MetadataCalls { get; private set; }
        public bool ReturnResults { get; init; }
        public HttpStatusCode MetadataStatus { get; init; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            Queries.Add(Uri.UnescapeDataString(request.RequestUri.PathAndQuery));
            var body = "[]";
            var status = HttpStatusCode.OK;
            if (path.EndsWith("/rpc/get_display_metadata", StringComparison.Ordinal))
            {
                MetadataCalls++;
                status = MetadataStatus;
                body = """
                    {"categories":[{"code":"U23_OPEN","name":"Under 23 Open","shortName":"U23","displayOrder":17,"gender":"open","minAge":17,"maxAge":22,"aliases":["Open U23"]}],
                     "sessionTypes":[{"code":"SUPER_FINAL","name":"Super Final","shortName":"Super","displayOrder":15,"abbreviation":"SF2","groupName":"Finals","aliases":["Last Run"]}],
                     "roundStatuses":[],"sessionStatuses":[]}
                    """;
            }
            else if (path.EndsWith("/categories", StringComparison.Ordinal))
            {
                body = "[{\"id\":30,\"code\":\"U23_OPEN\"}]";
            }
            else if (ReturnResults && path.EndsWith("/results", StringComparison.Ordinal))
            {
                body = """
                    [{"id":1,"rider_id":1,"session_id":10,"position":1,"race_time_ms":12000,"status":"Finished","is_classified":true,
                      "riders":{"id":1,"first_name":"Test","last_name":"Rider","nation":"POL","rider_team_history":[]},
                      "teams":null,"result_points":[],"time_splits":[],
                      "sessions":{"id":10,"round_id":4,"category_id":30,"session_type":"SUPER_FINAL","session_date":"2026-01-01",
                        "round_categories":{"round_id":4,"category_id":30,"rounds":{"id":4,"season_year":2026,"tracks":null},
                          "categories":{"id":30,"code":"U23_OPEN","name":"Under 23 Open"}}}}]
                    """;
            }

            return Task.FromResult(
                new HttpResponseMessage(status) { Content = new StringContent(body) }
            );
        }
    }
}
