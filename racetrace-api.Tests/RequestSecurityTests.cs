using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RaceTrace.Api.Infrastructure;

namespace RaceTrace.Api.Tests;

public sealed class RequestSecurityTests
{
    [Fact]
    public async Task PublicRequestBudgetIsPerAddressAndRejectsWithoutQueueing()
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .AddRaceTraceHttp()
            .BuildServiceProvider();
        var limiter = services
            .GetRequiredService<IOptions<RateLimiterOptions>>()
            .Value.GlobalLimiter;
        Assert.NotNull(limiter);
        var context = Request("/api/v1/rounds", "203.0.113.1");
        for (var i = 0; i < 240; i++)
        {
            using var lease = await limiter.AcquireAsync(context);
            Assert.True(lease.IsAcquired);
        }
        using var rejected = await limiter.AcquireAsync(context);
        using var independent = await limiter.AcquireAsync(
            Request("/api/v1/rounds", "203.0.113.2")
        );
        Assert.False(rejected.IsAcquired);
        Assert.True(independent.IsAcquired);
    }

    [Theory]
    [InlineData("/api/v1/compare")]
    [InlineData("/api/v1/riders/18/overview/")]
    [InlineData("/api/v1/riders/18/metrics")]
    [InlineData("/api/v1/riders/18/venues")]
    public async Task HeavyRequestsHaveAnApplicationWideConcurrencyLimit(string heavyPath)
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .AddRaceTraceHttp()
            .BuildServiceProvider();
        var limiter = services
            .GetRequiredService<IOptions<RateLimiterOptions>>()
            .Value.GlobalLimiter;
        Assert.NotNull(limiter);
        var leases = new List<System.Threading.RateLimiting.RateLimitLease>();
        try
        {
            for (var i = 1; i <= 8; i++)
            {
                var lease = await limiter.AcquireAsync(
                    Request("/api/v1/ranking", $"203.0.113.{i}")
                );
                Assert.True(lease.IsAcquired);
                leases.Add(lease);
            }
            using var rejected = await limiter.AcquireAsync(Request(heavyPath, "203.0.113.20"));
            using var light = await limiter.AcquireAsync(Request("/api/v1/rounds", "203.0.113.21"));
            Assert.False(rejected.IsAcquired);
            Assert.True(light.IsAcquired);
            leases[0].Dispose();
            using var recovered = await limiter.AcquireAsync(
                Request("/api/v1/ranking", "203.0.113.22")
            );
            Assert.True(recovered.IsAcquired);
        }
        finally
        {
            leases.ForEach(lease => lease.Dispose());
        }
    }

    [Fact]
    public async Task HttpRejectionReturns429AndRetryAfterWhileHealthRemainsAvailable()
    {
        var builder = WebApplication.CreateBuilder(
            new WebApplicationOptions { EnvironmentName = "Testing" }
        );
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddRaceTraceHttp();
        builder.Services.Configure<RaceTrace.Api.Configuration.RequestLimitOptions>(limits =>
            limits.RequestsPerMinute = 1
        );
        await using var app = builder.Build();
        app.UseRateLimiter();
        app.MapGet("/api/v1/rounds", () => Results.Ok());
        app.MapHealthChecks("/health/live");
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var first = await client.GetAsync("/api/v1/rounds");
            using var rejected = await client.GetAsync("/api/v1/rounds");
            using var health = await client.GetAsync("/health/live");
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
            Assert.NotNull(rejected.Headers.RetryAfter);
            Assert.Contains("RATE_LIMITED", await rejected.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Theory]
    [InlineData("ChronoRaceJsonFetcher")]
    [InlineData("ChronoRaceLiveProxyService")]
    [InlineData("TissotTimingJsonFetcher")]
    public void TimingHttpClientsDoNotFollowRedirects(string clientName)
    {
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddRaceTraceApplication()
            .BuildServiceProvider();
        var handler = provider
            .GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(clientName);
        while (handler is DelegatingHandler delegating)
            handler = delegating.InnerHandler!;
        Assert.False(Assert.IsType<SocketsHttpHandler>(handler).AllowAutoRedirect);
    }

    [Theory]
    [InlineData("dev-read-secret", "")]
    [InlineData("", "")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task ProductionRefusesMissingWeakOrSharedSecrets(string read, string admin)
    {
        var builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings { EnvironmentName = Environments.Production }
        );
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ApiAccess:ReadSecret"] = read,
                ["Admin:ApiSecret"] = admin,
            }
        );
        builder.Services.AddRaceTraceConfiguration(builder.Configuration);
        using var host = builder.Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
    }

    [Theory]
    [InlineData("Production", false)]
    [InlineData("Production", true)]
    [InlineData("Development", false)]
    public async Task ValidProductionAndExistingDevelopmentConfigurationCanStart(
        string environment,
        bool administration
    )
    {
        var builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings { EnvironmentName = environment }
        );
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ApiAccess:ReadSecret"] =
                    environment == "Production" ? new string('r', 64) : "dev-read-secret",
                ["Admin:ApiSecret"] = administration ? new string('a', 64) : "",
            }
        );
        builder.Services.AddRaceTraceConfiguration(builder.Configuration);
        using var host = builder.Build();
        await host.StartAsync();
        await host.StopAsync();
    }

    private static HttpContext Request(string path, string address)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        return context;
    }
}
