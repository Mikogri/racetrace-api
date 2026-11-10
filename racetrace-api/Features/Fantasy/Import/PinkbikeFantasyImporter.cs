using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using RaceTrace.Api.Infrastructure;

namespace RaceTrace.Api.Features.Fantasy.Import;

public sealed class PinkbikeFantasyImporter(
    IHttpClientFactory httpClients,
    PinkbikeBrowserPage browser,
    ISupabaseRestClient supabase,
    FantasyPriceSnapshotStore store,
    IOptions<FantasyImportOptions> options,
    TimeProvider time,
    ILogger<PinkbikeFantasyImporter> logger,
    FantasyJobCoordinator? jobs = null
)
{
    public const string HttpClientName = "PinkbikeFantasy";
    private static readonly SemaphoreSlim SyncGate = new(1, 1);

    public Task<FantasyPriceSnapshot> SyncAsync(
        int season,
        string? upcomingRoundId = null,
        CancellationToken cancellationToken = default,
        string? runId = null
    ) =>
        RunGuardedAsync(
            season,
            async token =>
            {
                string html;
                var method = "http";
                try
                {
                    // Reserve most of the shared timeout for the browser fallback.
                    using var download = CancellationTokenSource.CreateLinkedTokenSource(token);
                    download.CancelAfter(
                        TimeSpan.FromSeconds(
                            Math.Min(10, Math.Clamp(options.Value.TimeoutSeconds, 5, 120) / 3.0)
                        )
                    );
                    using var client = httpClients.CreateClient(HttpClientName);
                    using var request = new HttpRequestMessage(
                        HttpMethod.Get,
                        PinkbikeFantasyParser.SourceUrl
                    );
                    request.Headers.UserAgent.ParseAdd(
                        "RaceTrace/1.0 (Fantasy price synchronization)"
                    );
                    request.Headers.Accept.ParseAdd("text/html");
                    using var response = await client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        download.Token
                    );
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentType?.MediaType != "text/html")
                        throw new InvalidDataException("Pinkbike returned a non-HTML response.");
                    await response.Content.LoadIntoBufferAsync(8_000_000, download.Token);
                    html = await response.Content.ReadAsStringAsync(download.Token);
                    // HTTP 200 can still contain a challenge page or an unusable catalogue.
                    _ = PinkbikeFantasyParser.Parse(
                        html,
                        season,
                        time.GetUtcNow(),
                        options.Value.MinimumRiderCount
                    );
                }
                catch (Exception exception)
                    when (!token.IsCancellationRequested
                        && exception
                            is HttpRequestException
                                or IOException
                                or InvalidDataException
                                or FormatException
                                or OperationCanceledException
                    )
                {
                    logger.LogInformation(
                        "Pinkbike HTTP acquisition failed ({FailureType}); trying Chromium",
                        exception.GetType().Name
                    );
                    html = await browser.FetchAsync(token);
                    method = "headless-browser";
                }
                // Mapping/storage failures must not trigger another network acquisition.
                return await ImportCoreAsync(html, season, upcomingRoundId, method, token);
            },
            cancellationToken,
            runId
        );

    // Explicit, audited imports of saved public HTML use the same lock, timeout and failure reporting.
    internal Task<FantasyPriceSnapshot> ImportHtmlAsync(
        string html,
        int season,
        string? upcomingRoundId,
        string acquisitionMethod,
        CancellationToken cancellationToken,
        string? runId = null
    ) =>
        RunGuardedAsync(
            season,
            token => ImportCoreAsync(html, season, upcomingRoundId, acquisitionMethod, token),
            cancellationToken,
            runId
        );

    private async Task<FantasyPriceSnapshot> RunGuardedAsync(
        int season,
        Func<CancellationToken, Task<FantasyPriceSnapshot>> import,
        CancellationToken cancellationToken,
        string? runId
    )
    {
        // Reject overlapping syncs instead of queueing another expensive HTTP+DB import.
        if (!await SyncGate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("A Fantasy price sync is already running.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(
                TimeSpan.FromSeconds(Math.Clamp(options.Value.TimeoutSeconds, 5, 120))
            );
            if (jobs is not null)
                return await jobs.RunAsync(
                    $"prices:{season}",
                    runId ?? "manual:" + Guid.NewGuid().ToString("N"),
                    "prices",
                    season.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    import,
                    store.SaveAsync,
                    timeout.Token
                );
            var result = await import(timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            await store.SaveAsync(result, timeout.Token);
            return result;
        }
        catch (Exception exception)
            when (exception
                    is HttpRequestException
                        or InvalidDataException
                        or FormatException
                        or OperationCanceledException
                        or IOException
            )
        {
            logger.LogWarning(
                exception,
                "Pinkbike Fantasy sync failed for season {Season}; keeping the last successful snapshot",
                season
            );
            var message = exception switch
            {
                OperationCanceledException =>
                    "Fantasy price synchronization was cancelled or timed out.",
                HttpRequestException { StatusCode: HttpStatusCode.Forbidden } =>
                    "Pinkbike blocked browser access (HTTP 403 / verification required). The last successful prices are preserved.",
                _ =>
                    "Could not synchronize Pinkbike prices. The last successful snapshot is preserved.",
            };
            await store.RecordAttemptAsync(season, message, CancellationToken.None);
            throw;
        }
        finally
        {
            SyncGate.Release();
        }
    }

    private async Task<FantasyPriceSnapshot> ImportCoreAsync(
        string html,
        int season,
        string? upcomingRoundId,
        string acquisitionMethod,
        CancellationToken cancellationToken
    )
    {
        var fetchedAt = time.GetUtcNow();
        var athletes = PinkbikeFantasyParser.Parse(
            html,
            season,
            fetchedAt,
            options.Value.MinimumRiderCount
        );
        var previous = await store.GetLatestAsync(season, cancellationToken);
        if (previous is not null && athletes.Count < previous.Riders.Count * 0.75)
            throw new InvalidDataException(
                "Pinkbike athlete count unexpectedly fell by more than 25%; import requires review."
            );
        var identities = await LoadIdentitiesAsync(cancellationToken);
        var riders = FantasyRiderMapper.Map(athletes, identities, options.Value, previous);
        var hash = Convert
            .ToHexString(
                SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(riders))
            )[..16]
            .ToLowerInvariant();
        var snapshot = new FantasyPriceSnapshot(
            $"{season}-{fetchedAt:yyyyMMddTHHmmssfffffff}-{hash}",
            season,
            upcomingRoundId,
            fetchedAt,
            riders,
            PinkbikeFantasyParser.SourceUrl,
            acquisitionMethod
        )
        {
            DataVersion = 1,
        };
        return snapshot;
    }

    private async Task<IReadOnlyList<FantasyRiderIdentity>> LoadIdentitiesAsync(
        CancellationToken cancellationToken
    )
    {
        const int pageSize = 1000;
        var identities = new List<FantasyRiderIdentity>();
        for (var offset = 0; ; offset += pageSize)
        {
            var page = await supabase.GetAsync(
                new SupabaseQuery("riders")
                    .Select("id,first_name,last_name,gender")
                    .Order("id", false)
                    .Limit(pageSize)
                    .Offset(offset),
                cancellationToken: cancellationToken
            );
            identities.AddRange(
                page.Data.Select(row => new FantasyRiderIdentity(
                    row["id"]?.ToString() ?? "",
                    $"{row["first_name"]} {row["last_name"]}".Trim(),
                    row["gender"]?.ToString()
                ))
            );
            if (page.Data.Count < pageSize)
                return identities;
            if (offset >= 100_000)
                throw new InvalidDataException(
                    "Rider mapping exceeded the expected catalogue size."
                );
        }
    }
}
