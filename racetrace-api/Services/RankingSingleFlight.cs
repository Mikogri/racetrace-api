using System.Collections.Concurrent;
using System.Diagnostics;
using RaceTrace.Api.Infrastructure.Telemetry;

namespace RaceTrace.Api.Services;

public sealed class RankingSingleFlight
{
    private readonly ConcurrentDictionary<string, Lazy<Task<RankingSnapshot>>> _flights =
        new(StringComparer.Ordinal);

    internal async Task<RankingSnapshot> RunAsync(
        string key,
        Func<Task<RankingSnapshot>> factory,
        CancellationToken cancellationToken)
    {
        var candidate = new Lazy<Task<RankingSnapshot>>(
            () => InvokeAsync(factory),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var flight = _flights.GetOrAdd(key, candidate);
        var task = flight.Value;

        var shared = !ReferenceEquals(candidate, flight);
        var measured = ApiTelemetry.DetailedEnabled;
        if (measured) ApiTelemetry.FlightCalls.Add(1, new KeyValuePair<string, object?>("outcome", shared ? "shared" : "new"));
        if (!shared)
        {
            _ = RemoveWhenCompletedAsync(key, flight, task);
        }

        var started = Stopwatch.GetTimestamp();
        if (shared && measured) ApiTelemetry.FlightWaiters.Add(1);
        try
        {
            return await task.WaitAsync(cancellationToken);
        }
        finally
        {
            if (shared && measured)
            {
                ApiTelemetry.FlightWaiters.Add(-1);
                ApiTelemetry.FlightWaitDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
            }
        }
    }

    private static async Task<RankingSnapshot> InvokeAsync(Func<Task<RankingSnapshot>> factory) =>
        await factory();

    private async Task RemoveWhenCompletedAsync(
        string key,
        Lazy<Task<RankingSnapshot>> flight,
        Task<RankingSnapshot> task)
    {
        try
        {
            await task;
        }
        catch
        {
            // Every waiter observes the original failure; cleanup only removes the completed flight.
        }
        finally
        {
            _flights.TryRemove(new KeyValuePair<string, Lazy<Task<RankingSnapshot>>>(key, flight));
        }
    }
}
