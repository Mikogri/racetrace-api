using Microsoft.Extensions.Options;
using UCI_Stats_API.Features.Fantasy.Import;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Features.Fantasy;

/// <summary>Opt-in background refresh; ordinary page reads never launch simulations or scrape Pinkbike.</summary>
public sealed class FantasySyncHostedService(IServiceScopeFactory scopes, IOptions<FantasyOptions> options,
    TimeProvider time, ILogger<FantasySyncHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.ScheduledSyncEnabled) return;
        using var timer = new PeriodicTimer(options.Value.SyncInterval, time);
        do
        {
            using var scope = scopes.CreateScope();
            try
            {
                var fantasy = scope.ServiceProvider.GetRequiredService<FantasyService>();
                var round = await fantasy.NextRoundAsync(stoppingToken);
                try
                {
                    await scope.ServiceProvider.GetRequiredService<PinkbikeFantasyImporter>()
                        .SyncAsync(time.GetUtcNow().Year, round?.String("id"), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Fantasy price sync failed; refreshing independent race projections using the preserved price history.");
                }
                if (round is not null)
                    await scope.ServiceProvider.GetRequiredService<FantasyPreparationService>().PrepareAsync(round, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Fantasy refresh failed; the last successful price and projection snapshots are preserved.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
