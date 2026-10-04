using System.Text.Json.Nodes;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class RankingSingleFlightTests
{
    [Fact]
    public async Task ConcurrentCallsShareOneRunningOperation()
    {
        var singleFlight = new RankingSingleFlight();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        Task<RankingSnapshot> Factory()
        {
            Interlocked.Increment(ref calls);
            return CompleteAsync();
        }

        async Task<RankingSnapshot> CompleteAsync()
        {
            await release.Task;
            return Snapshot();
        }

        var first = singleFlight.RunAsync("same", Factory, CancellationToken.None);
        var second = singleFlight.RunAsync("same", Factory, CancellationToken.None);
        release.SetResult();

        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, calls);
        Assert.Same(results[0], results[1]);
    }

    [Fact]
    public async Task CompletedOperationIsNotCached()
    {
        var singleFlight = new RankingSingleFlight();
        var calls = 0;

        Task<RankingSnapshot> Factory()
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Snapshot());
        }

        await singleFlight.RunAsync("same", Factory, CancellationToken.None);
        await singleFlight.RunAsync("same", Factory, CancellationToken.None);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task FailedOperationCanBeRetried()
    {
        var singleFlight = new RankingSingleFlight();
        var calls = 0;

        Task<RankingSnapshot> Factory()
        {
            var attempt = Interlocked.Increment(ref calls);
            return attempt == 1
                ? Task.FromException<RankingSnapshot>(new InvalidOperationException("failed"))
                : Task.FromResult(Snapshot());
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            singleFlight.RunAsync("same", Factory, CancellationToken.None));
        await singleFlight.RunAsync("same", Factory, CancellationToken.None);

        Assert.Equal(2, calls);
    }

    private static RankingSnapshot Snapshot() => new([], [], [], [], []);
}
