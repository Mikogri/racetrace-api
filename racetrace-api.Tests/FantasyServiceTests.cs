using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using RaceTrace.Api.Features.Fantasy;
using RaceTrace.Api.Features.Fantasy.Import;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Infrastructure.Calculations;

namespace RaceTrace.Api.Tests;

public sealed class FantasyServiceTests
{
    [Theory]
    [InlineData("ready", true)]
    [InlineData("unknown-start-list", true)]
    [InlineData("unknown-start-list-injury", false)]
    [InlineData("stale-prices", true)]
    [InlineData("overdue-forecast", true)]
    [InlineData("other-round-prices", false)]
    [InlineData("training-exclusion", false)]
    [InlineData("training-empty", true)]
    [InlineData("missing-mapping", false)]
    [InlineData("wrong-category", false)]
    [InlineData("injury", false)]
    [InlineData("outside-start-list", false)]
    [InlineData("stale-model", false)]
    [InlineData("stale-dependency", false)]
    [InlineData("legacy-fingerprint", true)]
    [InlineData("legacy-fingerprint-strict", false)]
    [InlineData("changed-source", false)]
    public async Task RecommendationRequiresCompleteVerifiedInputs(string scenario, bool expected)
    {
        using var fixture = new Fixture(scenario);
        await fixture.SeedAsync(scenario);
        var response = await fixture.Service.CurrentAsync(default);
        Assert.Equal(expected, response.Recommendation is not null);
        Assert.Equal(6, response.Riders.Count);
        if (expected)
        {
            Assert.Equal(6, response.Recommendation!.Riders.Count);
            Assert.Equal(4, response.Recommendation.Riders.Count(rider => rider.Gender == "male"));
            Assert.Equal(2, response.Recommendation.Riders.Count(rider => rider.Gender == "female"));
            Assert.Equal(1_200_000, response.Recommendation.TotalPrice);
            Assert.Equal(300_000, response.Recommendation.RemainingBudget);
            Assert.Equal(600, response.Recommendation.ExpectedFantasyPoints);
        }
        else Assert.DoesNotContain(response.Riders, rider => rider.IsRecommended);
        if (!scenario.StartsWith("legacy-fingerprint", StringComparison.Ordinal) && scenario is not ("stale-model" or "stale-dependency" or "changed-source"))
        {
            Assert.NotNull(response.Calculation);
            Assert.Equal("stored-effective-config", response.Calculation.ConfigurationHash);
            Assert.Equal(123, response.Calculation.SimulationSeed);
            Assert.Equal(2_000, response.Calculation.SimulationIterations);
            Assert.Equal(FantasyCalibration.Version, response.Calculation.AlgorithmVersion);
        }
        else Assert.Null(response.Calculation);
        // Reads use the calendar/cache, not the importer, expensive metrics or race simulation RPC.
        Assert.Equal(1, fixture.Database.CalendarReads);
        await fixture.Service.CurrentAsync(default);
        Assert.Equal(1, fixture.Database.CalendarReads);
    }

    [Fact]
    public async Task AlreadyStartedRoundIsNotAnUpcomingFantasyRound()
    {
        using var fixture = new Fixture("ready");
        fixture.Database.RoundAlreadyStarted = true;
        var response = await fixture.Service.CurrentAsync(default);
        Assert.Null(response.Round);
        Assert.Null(response.Recommendation);
    }

    [Fact]
    public async Task LiveRoundRemainsTheFantasyTargetAfterTrainingStarts()
    {
        using var fixture = new Fixture("unknown-start-list");
        fixture.Database.RoundAlreadyStarted = true;
        fixture.Database.RoundLive = true;
        var response = await fixture.Service.CurrentAsync(default);
        Assert.Equal("37", response.Round?.RoundId);
    }

    [Fact]
    public async Task MissingPricesReturnsDiagnosticsWithoutInventedRiders()
    {
        using var fixture = new Fixture("ready");
        var response = await fixture.Service.CurrentAsync(default);
        Assert.Empty(response.Riders);
        Assert.Null(response.Recommendation);
        Assert.Contains(response.Warnings, warning => warning.Contains("prices have not been imported"));
    }

    [Fact]
    public async Task PublicContractsKeepCamelCaseWithGlobalSnakeCaseSerialization()
    {
        using var fixture = new Fixture("ready");
        await fixture.SeedAsync("ready");
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var json = JsonSerializer.SerializeToElement(await fixture.Service.CurrentAsync(default), options);
        Assert.Equal("p0", json.GetProperty("riders")[0].GetProperty("pinkbikeExternalId").GetString());
        Assert.True(json.TryGetProperty("priceSnapshotId", out _));
        Assert.True(json.GetProperty("round").TryGetProperty("roundId", out _));
        var rules = JsonSerializer.SerializeToElement(FantasyService.Rules(), options);
        Assert.Equal(30, rules.GetProperty("finalPlacementPoints").GetArrayLength());
        Assert.Equal(500, rules.GetProperty("finalPlacementPoints")[0].GetProperty("womenPoints").GetInt32());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "racetrace-fantasy-service-" + Guid.NewGuid().ToString("N"));
        private readonly ServiceProvider _provider;
        private readonly IServiceScope _scope;
        private readonly FantasyEventOptions? _event;
        private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-16T12:00:00Z");
        public CalendarClient Database { get; } = new();
        public FantasyService Service => _scope.ServiceProvider.GetRequiredService<FantasyService>();

        public Fixture(string scenario)
        {
            _event = scenario.StartsWith("unknown-start-list", StringComparison.Ordinal) || scenario.StartsWith("training-", StringComparison.Ordinal) ? null : new FantasyEventOptions
            {
                Q1BonusApplies = true,
                StartListRiderIds = Enumerable.Range(0, scenario == "outside-start-list" ? 5 : 6).Select(index => index.ToString()).ToList(),
                StartListSourceUrl = "https://example.org/verified-test-start-list",
                StartListFetchedAtUtc = Now.AddHours(-1)
            };
            var services = new ServiceCollection().AddLogging();
            services.AddRaceTraceConfiguration(new ConfigurationBuilder().Build()).AddSupabaseInfrastructure().AddRaceTraceApplication();
            services.AddSingleton<ISupabaseRestClient>(Database);
            services.AddSingleton<TimeProvider>(new FixedTime(Now));
            services.AddSingleton<IHostEnvironment>(new TestEnvironment(_directory));
            services.Configure<FantasyOptions>(options => { if (_event is not null) options.Events["37"] = _event; });
            if (scenario == "legacy-fingerprint-strict") services.Configure<CacheCoherenceOptions>(options => options.Enabled = true);
            _provider = services.BuildServiceProvider();
            _scope = _provider.CreateScope();
        }

        public async Task SeedAsync(string scenario)
        {
            var riders = Enumerable.Range(0, 6).Select(index => new PinkbikeFantasyRider("p" + index,
                scenario == "missing-mapping" && index == 0 ? null : index.ToString(), "Rider " + index,
                index < 4 ? "male" : "female", 200_000, 40, scenario.Contains("injury", StringComparison.Ordinal) && index == 0, Now,
                "mapped", null, [])).ToArray();
            await _provider.GetRequiredService<FantasyPriceSnapshotStore>().SaveAsync(new("prices", 2026, scenario == "other-round-prices" ? "36" : "37",
                scenario == "stale-prices" ? Now.AddDays(-2) : Now, riders, FantasyRules2026.AthletesUrl, "test"));
            var projections = riders.Select((rider, index) => new FantasyPreparedRider(index.ToString(), rider.Name, "FRA",
                scenario == "wrong-category" && index == 0 ? "female" : rider.Gender, 90, 2, 90, 10, 100)).ToArray();
            await _provider.GetRequiredService<FantasyPreparedStore>().WriteAsync(new(2026, "37", Now.AddDays(10), scenario == "overdue-forecast" ? Now.AddDays(-1) : Now,
                scenario == "stale-model" ? "old" : FantasyRacePrediction.EngineVersion, FantasyCalibration.Version,
                FantasyRules2026.Rules.Version, FantasyPreparationService.EventFingerprint(_event), projections, [], [],
                DataRevision: scenario == "changed-source" ? "old-data" : await _scope.ServiceProvider.GetRequiredService<FantasyRacePrediction>().DataRevisionAsync(default),
                TrainingFields: scenario == "training-exclusion" ? [new("male", "test", "https://example.org/training", Now, ["0", "1", "2"])] : [],
                CalculationFingerprint: scenario.StartsWith("legacy-fingerprint", StringComparison.Ordinal) ? null :
                    scenario == "stale-dependency" ? "previous-rating-config" : AlgorithmCatalog.Current.Fingerprint("fantasy"),
                ConfigurationHash: "stored-effective-config", SimulationSeed: 123, SimulationIterations: 2_000));
        }

        public void Dispose()
        {
            _scope.Dispose();
            _provider.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "FantasyTests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    private sealed class CalendarClient : ISupabaseRestClient
    {
        public int CalendarReads { get; private set; }
        public bool RoundAlreadyStarted { get; set; }
        public bool RoundLive { get; set; }
        public Task<SupabaseQueryResult> GetAsync(SupabaseQuery query, bool count = false, SupabaseRole role = SupabaseRole.Anon, CancellationToken cancellationToken = default)
        {
            if (count)
            {
                Assert.Contains(query.Table, new[] { "rounds", "sessions", "results", "time_splits" });
                return Task.FromResult(new SupabaseQueryResult([new JsonObject { ["id"] = 1, ["updated_at"] = "2026-09-01" }], 100));
            }
            Assert.Equal("rounds", query.Table);
            CalendarReads++;
            var round = JsonNode.Parse("""
                {"id":"37","season_year":2026,"round_number":8,"start_date":"2026-09-26T00:00:00Z","end_date":"2026-09-27T00:00:00Z",
                "competition_levels":{"code":"world_cup"},"tracks":{"venue":"Whistler","name":"1199"}}
                """)!.AsObject();
            if (RoundAlreadyStarted) round["start_date"] = "2026-09-15T00:00:00Z";
            if (RoundLive) round["status"] = "live";
            return Task.FromResult(new SupabaseQueryResult([round], null));
        }
        public Task<JsonObject?> GetSingleAsync(SupabaseQuery query, SupabaseRole role = SupabaseRole.Anon, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JsonObject>> PostAsync(string table, JsonNode payload, SupabaseRole role, string? onConflict = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JsonObject>> PatchAsync(string table, JsonObject payload, SupabaseQuery query, SupabaseRole role, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string table, SupabaseQuery query, SupabaseRole role, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JsonNode?> RpcAsync(string functionName, JsonObject payload, SupabaseRole role, CancellationToken cancellationToken = default) =>
            functionName == "get_calculation_revisions" ? Task.FromResult<JsonNode?>(new JsonObject { ["all"] = 1L, ["global"] = 1L }) : throw new NotSupportedException();
    }
}
