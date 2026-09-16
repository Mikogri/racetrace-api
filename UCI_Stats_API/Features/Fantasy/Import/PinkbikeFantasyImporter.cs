using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using UCI_Stats_API.Infrastructure;

namespace UCI_Stats_API.Features.Fantasy.Import;

public sealed class PinkbikeFantasyImporter(
    IHttpClientFactory httpClients,
    ISupabaseRestClient supabase,
    FantasyPriceSnapshotStore store,
    IOptions<FantasyImportOptions> options,
    TimeProvider time,
    ILogger<PinkbikeFantasyImporter> logger)
{
    public const string HttpClientName = "PinkbikeFantasy";
    private static readonly SemaphoreSlim SyncGate = new(1, 1);

    public Task<FantasyPriceSnapshot> SyncAsync(int season, string? upcomingRoundId = null, CancellationToken cancellationToken = default) =>
        RunGuardedAsync(season, async token =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, PinkbikeFantasyParser.SourceUrl);
            request.Headers.UserAgent.ParseAdd("RaceTrace/1.0 (Fantasy price synchronization)");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            using var client = httpClients.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentType?.MediaType != "text/html")
                throw new InvalidDataException("Pinkbike returned a non-HTML response.");
            await response.Content.LoadIntoBufferAsync(8_000_000, token);
            var html = await response.Content.ReadAsStringAsync(token);
            return await ImportCoreAsync(html, season, upcomingRoundId, "http", token);
        }, cancellationToken);

    // Explicit, audited imports of saved public HTML use the same lock, timeout and failure reporting.
    internal Task<FantasyPriceSnapshot> ImportHtmlAsync(string html, int season, string? upcomingRoundId, string acquisitionMethod, CancellationToken cancellationToken) =>
        RunGuardedAsync(season, token => ImportCoreAsync(html, season, upcomingRoundId, acquisitionMethod, token), cancellationToken);

    private async Task<FantasyPriceSnapshot> RunGuardedAsync(int season, Func<CancellationToken, Task<FantasyPriceSnapshot>> import, CancellationToken cancellationToken)
    {
        // Reject overlapping syncs instead of queueing another expensive HTTP+DB import.
        if (!await SyncGate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("A Fantasy price sync is already running.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.Value.TimeoutSeconds, 5, 120)));
            return await import(timeout.Token);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or FormatException or OperationCanceledException or IOException)
        {
            logger.LogWarning(exception, "Pinkbike Fantasy sync failed for season {Season}; keeping the last successful snapshot", season);
            var message = exception is OperationCanceledException ? "Fantasy price synchronization was cancelled or timed out." : "Could not synchronize Pinkbike prices. The last successful snapshot is preserved.";
            await store.RecordAttemptAsync(season, message, CancellationToken.None);
            throw;
        }
        finally { SyncGate.Release(); }
    }

    private async Task<FantasyPriceSnapshot> ImportCoreAsync(string html, int season, string? upcomingRoundId, string acquisitionMethod, CancellationToken cancellationToken)
    {
        var fetchedAt = time.GetUtcNow();
        var athletes = PinkbikeFantasyParser.Parse(html, season, fetchedAt, options.Value.MinimumRiderCount);
        var previous = await store.GetLatestAsync(season, cancellationToken);
        if (previous is not null && athletes.Count < previous.Riders.Count * 0.75)
            throw new InvalidDataException("Pinkbike athlete count unexpectedly fell by more than 25%; import requires review.");
        var identities = await LoadIdentitiesAsync(cancellationToken);
        var riders = FantasyRiderMapper.Map(athletes, identities, options.Value, previous);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(html)))[..16].ToLowerInvariant();
        var snapshot = new FantasyPriceSnapshot($"{season}-{fetchedAt:yyyyMMddTHHmmssfffffff}-{hash}", season, upcomingRoundId,
            fetchedAt, riders, PinkbikeFantasyParser.SourceUrl, acquisitionMethod);
        await store.SaveAsync(snapshot, cancellationToken);
        return snapshot;
    }

    private async Task<IReadOnlyList<FantasyRiderIdentity>> LoadIdentitiesAsync(CancellationToken cancellationToken)
    {
        const int pageSize = 1000;
        var identities = new List<FantasyRiderIdentity>();
        for (var offset = 0; ; offset += pageSize)
        {
            var page = await supabase.GetAsync(new SupabaseQuery("riders").Select("id,first_name,last_name,gender").Order("id", false).Limit(pageSize).Offset(offset), cancellationToken: cancellationToken);
            identities.AddRange(page.Data.Select(row => new FantasyRiderIdentity(row["id"]?.ToString() ?? "",
                $"{row["first_name"]} {row["last_name"]}".Trim(), row["gender"]?.ToString())));
            if (page.Data.Count < pageSize) return identities;
            if (offset >= 100_000) throw new InvalidDataException("Rider mapping exceeded the expected catalogue size.");
        }
    }
}
