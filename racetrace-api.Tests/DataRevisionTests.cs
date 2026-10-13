using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Caching.Memory;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Infrastructure.Calculations;
using RaceTrace.Api.Services;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Tests;

public sealed class DataRevisionTests
{
    [Fact]
    public async Task RankingDoesNotPublishSnapshotIfSourceChangesAndCanRetryAfterFailure()
    {
        var rpc = new RevisionRpc { ChangeOnRead = true };
        using var services = Services(rpc, new Clock());
        var revisions = services.GetRequiredService<DataRevisionService>();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var domain = new DomainQueryService(rpc);
        var ranking = new RankingService(new(), domain, new MetricsService(domain, new PaceService(rpc)), new(), cache, revisions);
        var filters = new RiderMetricsContext(2026, "EM", null, null, null, null, null, null, null, null, null);
        await Assert.ThrowsAsync<ApiException>(() => ranking.BuildSnapshotAsync(filters, default));
        Assert.Equal(0, cache.Count);
        rpc.ChangeOnRead = false;
        var valid = await ranking.BuildSnapshotAsync(filters, default);
        var reused = await ranking.BuildSnapshotAsync(filters, default);
        Assert.Same(valid, reused);
        Assert.NotNull(valid.Metadata);
        Assert.Equal(1, cache.Count);
        Assert.Equal(2, rpc.SourceReads);
    }

    [Fact]
    public async Task RankingRefreshRecomputesSnapshotAndReplacesMemoryCacheEntry()
    {
        var rpc = new RevisionRpc();
        using var services = Services(rpc, new Clock());
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var domain = new DomainQueryService(rpc);
        var ranking = new RankingService(new(), domain, new MetricsService(domain, new PaceService(rpc)), new(), cache,
            services.GetRequiredService<DataRevisionService>());
        var filters = new RiderMetricsContext(2026, "EM", null, null, null, null, null, null, null, null, null);

        var original = await ranking.BuildSnapshotAsync(filters, default);
        var refreshed = await ranking.BuildSnapshotAsync(filters, default, forceRefresh: true);
        var reused = await ranking.BuildSnapshotAsync(filters, default);

        Assert.NotSame(original, refreshed);
        Assert.Same(refreshed, reused);
        Assert.Equal(2, rpc.SourceReads);
    }

    [Fact]
    public async Task RankingRefreshBypassesOutputCacheWithoutAffectingOtherRoutes()
    {
        var policy = new ApiReadOutputCachePolicy(TimeSpan.FromSeconds(30));
        static OutputCacheContext Context() => new()
        {
            HttpContext = new DefaultHttpContext()
        };
        var ranking = Context();
        ranking.HttpContext.Request.Method = "GET";
        ranking.HttpContext.Request.Path = "/api/v1/ranking";
        ranking.HttpContext.Request.QueryString = new("?refresh=1");
        await policy.CacheRequestAsync(ranking, default);
        Assert.False(ranking.AllowCacheLookup);
        Assert.False(ranking.AllowCacheStorage);

        var other = Context();
        other.HttpContext.Request.Method = "GET";
        other.HttpContext.Request.Path = "/api/v1/riders";
        other.HttpContext.Request.QueryString = new("?refresh=1");
        await policy.CacheRequestAsync(other, default);
        Assert.True(other.AllowCacheLookup);
    }

    [Theory]
    [InlineData("/API/V1/RANKING")]
    [InlineData("/api/v1/riders")]
    public async Task ResponseFilterPreservesCachedTimestampWithoutPhysicalCheckPerHit(string path)
    {
        var rpc = new RevisionRpc();
        using var services = Services(rpc, new Clock());
        var provenance = new CalculationProvenance();
        var computedAt = DateTimeOffset.Parse("2026-10-09T12:00:00Z");
        provenance.Include(new("rating=1", "hash", "revision", computedAt));
        var http = new DefaultHttpContext();
        http.Request.Method = "GET";
        http.Request.Path = path;
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var filter = new CalculationResponseFilter(services.GetRequiredService<DataRevisionService>(), provenance);
        for (var i = 0; i < 20; i++)
            await filter.OnActionExecutionAsync(new ActionExecutingContext(action, [], new Dictionary<string, object?>(), new object()),
                () => Task.FromResult(new ActionExecutedContext(action, [], new object())));
        Assert.Equal(1, rpc.Calls);
        Assert.Equal(computedAt.ToString("O"), http.Response.Headers["X-Computed-At"]);
        Assert.False(string.IsNullOrEmpty(http.Response.Headers["X-Data-Revision"]));
    }

    [Fact]
    public async Task ResponseFilterRejectsRawCalculationSpanningCommittedWrite()
    {
        var rpc = new RevisionRpc();
        using var services = Services(rpc, new Clock());
        var http = new DefaultHttpContext();
        http.Request.Method = "GET";
        http.Request.Path = "/api/v1/analytics/round-profile";
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var filter = new CalculationResponseFilter(services.GetRequiredService<DataRevisionService>(), new());
        await Assert.ThrowsAsync<ApiException>(() => filter.OnActionExecutionAsync(
            new ActionExecutingContext(action, [], new Dictionary<string, object?>(), new object()),
            () => { rpc.Revision++; return Task.FromResult(new ActionExecutedContext(action, [], new object())); }));
        Assert.False(http.Response.Headers.ContainsKey("X-Computed-At"));
    }

    [Theory]
    [InlineData("?season=02026", "season:2026")]
    [InlineData("?season=2026&comparison=previousFilters&comparisonSeason=%20", "all")]
    [InlineData("?season=invalid", "all")]
    public void ResponseScopesNormalizeNumericFiltersAndTreatEmptyComparisonAsUnrestricted(string query, string expected)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new(query);
        Assert.Equal([expected], RequestCalculationIdentity.Scopes(context));
    }

    [Fact]
    public void ResponseScopesNormalizeRouteIds()
    {
        var context = new DefaultHttpContext();
        context.Request.RouteValues["roundId"] = "0007";
        Assert.Equal(["round:7"], RequestCalculationIdentity.Scopes(context));
    }

    [Fact]
    public async Task CompletionCheckCannotReuseAnotherRequestsCheckFromBeforeAWrite()
    {
        var rpc = new RevisionRpc();
        using var services = Services(rpc, new Clock());
        var revisions = services.GetRequiredService<DataRevisionService>();
        var observed = await revisions.GetAsync();
        await revisions.GetAsync(forceRefresh: true);
        rpc.Revision++;
        var completed = await revisions.GetAsync(forceRefresh: true);
        Assert.NotEqual(observed.ForScopes(["all"]), completed.ForScopes(["all"]));
        Assert.Equal(3, rpc.Calls);
    }

    [Fact]
    public async Task ConcurrentReadsShareCheckUntilFiveSecondsAndFailuresDoNotExtendFreshness()
    {
        var rpc = new RevisionRpc();
        var clock = new Clock();
        using var services = Services(rpc, clock);
        var revisions = services.GetRequiredService<DataRevisionService>();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => revisions.GetAsync()));
        Assert.Equal(1, rpc.Calls);
        clock.Now += TimeSpan.FromSeconds(4.999);
        await revisions.GetAsync();
        Assert.Equal(1, rpc.Calls);
        clock.Now += TimeSpan.FromMilliseconds(1);
        rpc.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => revisions.GetAsync());
        rpc.Fail = false;
        rpc.Revision++;
        await revisions.GetAsync();
        Assert.Equal(3, rpc.Calls);
    }

    [Fact]
    public async Task OutputCacheIdentityChangesOnlyForRelevantScopeAndDoesNotStoreRacingResponse()
    {
        var rpc = new RevisionRpc();
        var clock = new Clock();
        using var services = Services(rpc, clock);
        var policy = new ApiReadOutputCachePolicy(TimeSpan.FromMinutes(5));
        OutputCacheContext Context() => new()
        {
            HttpContext = new DefaultHttpContext { RequestServices = services }
        };
        var first = Context();
        first.HttpContext.Request.Method = "GET";
        first.HttpContext.Request.Path = "/api/v1/ranking";
        first.HttpContext.Request.QueryString = new("?season=2026");
        await policy.CacheRequestAsync(first, default);
        var before = first.CacheVaryByRules.VaryByValues["data-revision"];
        rpc.Revision++;
        await policy.ServeResponseAsync(first, default);
        Assert.False(first.AllowCacheStorage);
        var second = Context();
        second.HttpContext.Request.Method = "GET";
        second.HttpContext.Request.QueryString = new("?season=2026");
        await policy.CacheRequestAsync(second, default);
        Assert.NotEqual(before, second.CacheVaryByRules.VaryByValues["data-revision"]);
    }

    [Fact]
    public async Task DisabledRevisionServiceDoesNotContactDatabase()
    {
        var rpc = new RevisionRpc { Fail = true };
        using var services = Services(rpc, new Clock(), false);
        Assert.Same(DataRevisionSnapshot.Unversioned, await services.GetRequiredService<DataRevisionService>().GetAsync());
        Assert.Equal(0, rpc.Calls);
    }

    private static ServiceProvider Services(RevisionRpc rpc, Clock clock, bool enabled = true) => new ServiceCollection()
        .AddSingleton<ISupabaseRestClient>(rpc).AddSingleton<TimeProvider>(clock)
        .AddSingleton(Options.Create(new CacheCoherenceOptions { Enabled = enabled }))
        .AddSingleton<DataRevisionService>().BuildServiceProvider();

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-10T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RevisionRpc : ISupabaseRestClient
    {
        public long Revision = 1;
        public int Calls;
        public bool Fail;
        public bool ChangeOnRead;
        public int SourceReads;
        public Task<JsonNode?> RpcAsync(string functionName, JsonObject payload, SupabaseRole role, CancellationToken cancellationToken = default)
        {
            Calls++;
            Assert.Equal("get_calculation_revisions", functionName);
            Assert.Equal(SupabaseRole.Service, role);
            if (Fail) throw new IOException("test unavailable");
            return Task.FromResult(JsonNode.Parse($$"""{"all":{{Revision}},"global":1,"season:2026":{{Revision}}}"""));
        }
        public Task<SupabaseQueryResult> GetAsync(SupabaseQuery query, bool count = false, SupabaseRole role = SupabaseRole.Anon, CancellationToken cancellationToken = default)
        {
            SourceReads++;
            if (ChangeOnRead) Revision++;
            return Task.FromResult(new SupabaseQueryResult([], 0));
        }
        public Task<JsonObject?> GetSingleAsync(SupabaseQuery query, SupabaseRole role = SupabaseRole.Anon, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JsonObject>> PostAsync(string table, JsonNode payload, SupabaseRole role, string? onConflict = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JsonObject>> PatchAsync(string table, JsonObject payload, SupabaseQuery query, SupabaseRole role, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string table, SupabaseQuery query, SupabaseRole role, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
