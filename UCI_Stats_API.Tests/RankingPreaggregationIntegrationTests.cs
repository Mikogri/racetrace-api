using Microsoft.Extensions.Options;
using UCI_Stats_API.Configuration;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Models;
using UCI_Stats_API.Services;
using Xunit.Abstractions;

namespace UCI_Stats_API.Tests;

public sealed class RankingPreaggregationIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public RankingPreaggregationIntegrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PreaggregatesMatchRawRankingMetrics()
    {
        var url = Environment.GetEnvironmentVariable("SUPABASE_URL");
        var key = Environment.GetEnvironmentVariable("SUPABASE_SERVICE_ROLE_KEY");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var client = new SupabaseRestClient(
            new HttpClient(),
            Options.Create(new SupabaseOptions { Url = url, AnonKey = key, ServiceRoleKey = key }));
        var domain = new DomainQueryService(client);
        var metrics = new MetricsService(domain, new PaceService(client));
        var context = new RiderMetricsContext(null, "EM", null, null, null, null, null, null, null, null, null)
        {
            SeasonYears = [2025]
        };
        var sessions = await domain.GetMatchingSessionsAsync(context, SupabaseRole.Service);
        var preaggregated = await domain.GetRankingPreaggregatesAsync(sessions, role: SupabaseRole.Service);
        Assert.NotNull(preaggregated);

        var raw = await domain.GetRankingDataAsync(
            context,
            resolvedSessionIds: sessions.Select(session => session.String("id")).OfType<string>(),
            role: SupabaseRole.Service);
        var rawMetrics = await metrics.CalculatePerformanceByRiderAsync(
            raw.Results,
            context,
            resultsContainFullField: true,
            availableSessions: sessions,
            suppliedSplits: raw.Splits,
            suppliedFieldResults: raw.FieldResults);
        var aggregateMetrics = await metrics.CalculatePerformanceByRiderAsync(
            preaggregated!.Data.Results,
            context,
            resultsContainFullField: true,
            availableSessions: sessions,
            suppliedSplits: preaggregated.Data.Splits,
            suppliedFieldResults: preaggregated.Data.Results);
        var calculator = new DownhillPaceCalculator();
        var computedSessions = preaggregated.PaceSessions.Select(calculator.CalculatePrecomputedSession).ToArray();

        var firstSession = preaggregated.PaceSessions[0];
        _output.WriteLine($"sessions={sessions.Count}, results={preaggregated.Data.Results.Count}, splits={preaggregated.Data.Splits.Count}, rawSplits={raw.Splits.Count}, paceSessions={preaggregated.PaceSessions.Count}, representative={computedSessions.Count(value => value.IsRepresentative)}, sessionPaces={computedSessions.Count(value => value.Pace is not null)}, rawRiders={rawMetrics.Count}, aggregateRiders={aggregateMetrics.Count}, aggregateRatings={aggregateMetrics.Values.Count(value => value.Rating is not null)}, firstStatus={firstSession.Run.Status}, firstClassified={firstSession.Run.IsClassified}, firstSectors={firstSession.Sectors.Count}, firstTimes={string.Join(',', firstSession.Run.SectorTimes)}, firstSplit={preaggregated.Data.Splits.FirstOrDefault()}");
        Assert.NotEmpty(preaggregated.PaceSessions);
        Assert.Equal(rawMetrics.Keys.Order(), aggregateMetrics.Keys.Order());
        foreach (var riderId in rawMetrics.Keys)
        {
            Assert.Equal(rawMetrics[riderId].Rating, aggregateMetrics[riderId].Rating);
            Assert.Equal(rawMetrics[riderId].Pace, aggregateMetrics[riderId].Pace);
            Assert.Equal(rawMetrics[riderId].Consistency, aggregateMetrics[riderId].Consistency);
        }
    }
}
