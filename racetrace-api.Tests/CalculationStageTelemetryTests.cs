using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using RaceTrace.Api.Infrastructure.Supabase;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class CalculationStageTelemetryTests
{
    [Fact]
    public void SourceDecodingAndIncidentDetectionHaveDistinctBoundedStageNames()
    {
        var stages = new ConcurrentBag<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) =>
        {
            if (instrument.Name == "racetrace.operation.duration")
                meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>(
            (instrument, value, tags, state) =>
            {
                foreach (var tag in tags)
                    if (tag.Key == "operation" && tag.Value is string name)
                        stages.Add(name);
            }
        );
        listener.Start();
        CompactMetricsPackageParser.ParseRankingReadData([]);
        SectorIncidentDetector.Analyze([], new PaceOptions());
        Assert.Contains("input_decode", stages);
        Assert.Contains("incident_detection", stages);
    }
}
