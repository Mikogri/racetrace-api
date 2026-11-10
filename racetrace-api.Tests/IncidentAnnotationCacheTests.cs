using System.Text.Json;
using RaceTrace.Api.Infrastructure.Calculations;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class IncidentAnnotationCacheTests
{
    [Fact]
    public void StoredAnnotationsPreserveEveryCalculatedValue()
    {
        var calculator = new DownhillPaceCalculator(AlgorithmConfiguration.Pace);
        var input = new PaceSessionData(
            PaceSessionType.Final,
            Enumerable
                .Range(1, 30)
                .Select(i => new PaceRiderRun(
                    i.ToString(),
                    new double?[]
                    {
                        40 + i * .02,
                        45 + i * .03,
                        50 + i * .02 + (i == 1 ? 15 : 0),
                        55 + i * .01,
                        40 + i * .02,
                    },
                    FinishTimeSeconds: 230 + i * .10 + (i == 1 ? 15 : 0),
                    FinishRank: i
                ))
                .ToArray()
        );
        var baseline = calculator.PrepareSession(input);
        var payload = IncidentAnnotationCache.Encode(baseline);
        var stored = IncidentAnnotationCache.Decode(IncidentAnnotationCache.Version, payload);
        Assert.NotNull(stored);
        var cached = calculator.PrepareSession(input, stored);
        Assert.All(cached, run => Assert.NotNull(run.PreparedAnomalies));
        Assert.Equal(
            JsonSerializer.Serialize(baseline.Select(calculator.CalculatePrecomputedSession)),
            JsonSerializer.Serialize(cached.Select(calculator.CalculatePrecomputedSession))
        );
    }

    [Fact]
    public void NormalOnlyFieldsHaveACompactRepresentation()
    {
        var calculator = new DownhillPaceCalculator(AlgorithmConfiguration.Pace);
        var input = new PaceSessionData(
            PaceSessionType.Final,
            Enumerable
                .Range(1, 30)
                .Select(i => new PaceRiderRun(
                    i.ToString(),
                    new double?[]
                    {
                        40 * (1 + i * .01),
                        45 * (1 + i * .01),
                        50 * (1 + i * .01),
                        55 * (1 + i * .01),
                        40 * (1 + i * .01),
                    },
                    FinishTimeSeconds: 230 * (1 + i * .01)
                ))
                .ToArray()
        );
        var baseline = calculator.PrepareSession(input);
        var payload = IncidentAnnotationCache.Encode(baseline);
        Assert.True(
            payload.Length < 1024,
            $"Normal-sector diagnostics must not inflate the cache: {payload.Length}"
        );
        var stored = IncidentAnnotationCache.Decode(IncidentAnnotationCache.Version, payload);
        Assert.NotNull(stored);
        Assert.Equal(
            JsonSerializer.Serialize(baseline.Select(calculator.CalculatePrecomputedSession)),
            JsonSerializer.Serialize(
                calculator
                    .PrepareSession(input, stored)
                    .Select(calculator.CalculatePrecomputedSession)
            )
        );
    }

    [Theory]
    [InlineData("old-version", "not-base64")]
    [InlineData(null, "not-base64")]
    public void StaleVersionIsACacheMiss(string? version, string payload) =>
        Assert.Null(IncidentAnnotationCache.Decode(version, payload));

    [Fact]
    public void CorruptPayloadIsACacheMiss() =>
        Assert.Null(IncidentAnnotationCache.Decode(IncidentAnnotationCache.Version, "not-base64"));

    [Fact]
    public void CachedAnnotationsDoNotOverrideCustomCalculatorOptions()
    {
        var data = new PaceSessionData(
            PaceSessionType.Final,
            [new PaceRiderRun("1", new double?[] { 40, 45, 50 }, FinishTimeSeconds: 135)]
        );
        var cache = new Dictionary<string, SectorAnomaly[]>
        {
            ["1"] = Enumerable
                .Repeat(
                    new SectorAnomaly(SectorAnomalyKind.LikelyIncident, 1, 40, 99, "fixture", true),
                    3
                )
                .ToArray(),
        };
        var calculator = new DownhillPaceCalculator(new PaceOptions { MinimumFieldSize = 10 });
        var baseline = calculator.PrepareSession(data);
        var cached = calculator.PrepareSession(data, cache);
        Assert.Equal(
            JsonSerializer.Serialize(baseline.Select(calculator.CalculatePrecomputedSession)),
            JsonSerializer.Serialize(cached.Select(calculator.CalculatePrecomputedSession))
        );
    }
}
