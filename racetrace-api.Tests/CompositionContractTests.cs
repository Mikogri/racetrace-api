using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RaceTrace.Api.Configuration;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class CompositionContractTests
{
    [Theory]
    [InlineData("override", "override")]
    [InlineData("", "configured")]
    [InlineData("   ", "configured")]
    public void EnvironmentSecretKeepsExistingPrecedence(string environmentValue, string expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ApiAccess:ReadSecret"] = "configured",
                    ["Admin:ApiSecret"] = "configured",
                    ["API_READ_SECRET"] = environmentValue,
                    ["ADMIN_API_SECRET"] = environmentValue,
                }
            )
            .Build();
        var services = new ServiceCollection().AddRaceTraceConfiguration(configuration);
        using var provider = services.BuildServiceProvider();
        Assert.Equal(
            expected,
            provider.GetRequiredService<IOptions<ApiAccessOptions>>().Value.ReadSecret
        );
        Assert.Equal(
            expected,
            provider.GetRequiredService<IOptions<AdminOptions>>().Value.ApiSecret
        );
    }

    [Fact]
    public void ApplicationKeepsScopedQueriesAndSingletonCoordination()
    {
        var services = new ServiceCollection().AddRaceTraceApplication();
        Assert.Equal(
            ServiceLifetime.Scoped,
            Assert.Single(services, item => item.ServiceType == typeof(DomainQueryService)).Lifetime
        );
        Assert.Equal(
            ServiceLifetime.Scoped,
            Assert.Single(services, item => item.ServiceType == typeof(RidersService)).Lifetime
        );
        Assert.Equal(
            ServiceLifetime.Singleton,
            Assert
                .Single(services, item => item.ServiceType == typeof(RankingSingleFlight))
                .Lifetime
        );
    }
}
