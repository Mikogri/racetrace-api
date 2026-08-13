using UCI_Stats_API.Models;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Tests;

internal sealed class TestPointAwardService(bool pointSystemApplied = true) : IPointAwardService
{
    public List<string> RecalculatedSessionIds { get; } = [];

    public Task<PointAwardCalculationResult> RecalculateSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        RecalculatedSessionIds.Add(sessionId);
        return Task.FromResult(new PointAwardCalculationResult(
            sessionId,
            pointSystemApplied,
            0,
            0,
            0,
            null,
            null));
    }
}
