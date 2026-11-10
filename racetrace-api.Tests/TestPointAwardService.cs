using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

internal sealed class TestPointAwardService(bool pointSystemApplied = true) : IPointAwardService
{
    public List<string> RecalculatedSessionIds { get; } = [];

    public Task<PointAwardCalculationResult> RecalculateSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default
    )
    {
        RecalculatedSessionIds.Add(sessionId);
        return Task.FromResult(
            new PointAwardCalculationResult(sessionId, pointSystemApplied, 0, 0, 0, null, null)
        );
    }
}
