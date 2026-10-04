using System.Collections.Concurrent;

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

        if (ReferenceEquals(candidate, flight))
        {
            _ = RemoveWhenCompletedAsync(key, flight, task);
        }

        return await task.WaitAsync(cancellationToken);
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
