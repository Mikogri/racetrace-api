using Microsoft.Extensions.Options;
using RaceTrace.Api.Configuration;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;
using Xunit.Abstractions;

namespace RaceTrace.Api.Tests;

public sealed class RankingPreaggregationIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public RankingPreaggregationIntegrationTests(ITestOutputHelper output) => _output = output;

    public static TheoryData<int[]> SeasonRanges =>
        new()
        {
            new[] { 2022 },
            new[] { 2023 },
            new[] { 2024 },
            new[] { 2025 },
            new[] { 2026 },
            new[] { 2022, 2023, 2024, 2025, 2026 },
        };

    [Theory]
    [MemberData(nameof(SeasonRanges))]
    [Trait("Category", "Integration")]
    public async Task PreaggregatesMatchRawRankingMetrics(int[] seasonYears)
    {
        var url = Environment.GetEnvironmentVariable("SUPABASE_URL");
        var key = Environment.GetEnvironmentVariable("SUPABASE_SERVICE_ROLE_KEY");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var client = new SupabaseRestClient(
            new HttpClient(),
            Options.Create(
                new SupabaseOptions
                {
                    Url = url,
                    AnonKey = key,
                    ServiceRoleKey = key,
                }
            )
        );
        var domain = new DomainQueryService(client);
        var metrics = new MetricsService(domain, new PaceService(client));
        var context = new RiderMetricsContext(
            null,
            "EM",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null
        )
        {
            SeasonYears = seasonYears,
        };
        var sessions = await domain.GetMatchingSessionsAsync(context, SupabaseRole.Service);
        var preaggregated = await domain.GetRankingPreaggregatesAsync(
            sessions,
            role: SupabaseRole.Service
        );
        Assert.NotNull(preaggregated);

        var raw = await domain.GetRankingDataAsync(
            context,
            resolvedSessionIds: sessions.Select(session => session.String("id")).OfType<string>(),
            role: SupabaseRole.Service
        );
        var rawMetrics = await metrics.CalculatePerformanceByRiderAsync(
            raw.Results,
            context,
            resultsContainFullField: true,
            availableSessions: sessions,
            suppliedSplits: raw.Splits,
            suppliedFieldResults: raw.FieldResults
        );
        var aggregateMetrics = metrics.CalculatePrecomputedPerformanceByRider(
            preaggregated!.Data.Results,
            preaggregated.PaceSessions,
            sessions
        );
        _output.WriteLine(
            $"seasons={string.Join(',', seasonYears)}, sessions={sessions.Count}, results={preaggregated.Data.Results.Count}, splits={preaggregated.Data.Splits.Count}, riders={aggregateMetrics.Count}"
        );
        Assert.NotEmpty(preaggregated.PaceSessions);
        Assert.Equal(rawMetrics.Keys.Order(), aggregateMetrics.Keys.Order());
        foreach (var riderId in rawMetrics.Keys)
        {
            if (rawMetrics[riderId] != aggregateMetrics[riderId])
            {
                _output.WriteLine(
                    $"rider={riderId}; raw={rawMetrics[riderId]}; aggregate={aggregateMetrics[riderId]}"
                );
            }
            Assert.Equal(rawMetrics[riderId].Rating, aggregateMetrics[riderId].Rating);
            Assert.Equal(rawMetrics[riderId].RawRating, aggregateMetrics[riderId].RawRating);
            Assert.Equal(rawMetrics[riderId].Pace, aggregateMetrics[riderId].Pace);
            Assert.Equal(rawMetrics[riderId].RawPace, aggregateMetrics[riderId].RawPace);
            Assert.Equal(
                rawMetrics[riderId].PaceConfidence,
                aggregateMetrics[riderId].PaceConfidence
            );
            Assert.Equal(
                rawMetrics[riderId].PaceUsedSessions,
                aggregateMetrics[riderId].PaceUsedSessions
            );
            Assert.Equal(
                rawMetrics[riderId].PaceUsedFinals,
                aggregateMetrics[riderId].PaceUsedFinals
            );
            Assert.Equal(rawMetrics[riderId].Consistency, aggregateMetrics[riderId].Consistency);
            Assert.Equal(
                rawMetrics[riderId].ConsistencyConfidence,
                aggregateMetrics[riderId].ConsistencyConfidence
            );
            Assert.Equal(
                rawMetrics[riderId].ConsistencyUsedRuns,
                aggregateMetrics[riderId].ConsistencyUsedRuns
            );
            Assert.Equal(
                rawMetrics[riderId].ConsistencyUsedSectors,
                aggregateMetrics[riderId].ConsistencyUsedSectors
            );
            Assert.Equal(rawMetrics[riderId].ResultScore, aggregateMetrics[riderId].ResultScore);
            Assert.Equal(
                rawMetrics[riderId].ResultScoreConfidence,
                aggregateMetrics[riderId].ResultScoreConfidence
            );
            Assert.Equal(
                rawMetrics[riderId].ResultScoreUsedSessions,
                aggregateMetrics[riderId].ResultScoreUsedSessions
            );
        }
    }
}
