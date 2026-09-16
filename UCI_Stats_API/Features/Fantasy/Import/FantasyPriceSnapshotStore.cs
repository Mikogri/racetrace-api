using System.Text.Json;
using Microsoft.Extensions.Options;

namespace UCI_Stats_API.Features.Fantasy.Import;

/// <summary>Append-only snapshots. A failed import changes only sync status, never the last good data.</summary>
public sealed class FantasyPriceSnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _directory;
    private readonly FantasyImportOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<FantasyPriceSnapshotStore> _logger;

    public FantasyPriceSnapshotStore(IOptions<FantasyImportOptions> options, IHostEnvironment environment, TimeProvider time, ILogger<FantasyPriceSnapshotStore> logger)
    {
        _options = options.Value;
        _directory = Path.GetFullPath(_options.SnapshotDirectory, environment.ContentRootPath);
        _time = time;
        _logger = logger;
    }

    public Task<FantasyPriceSnapshot?> GetLatestAsync(int season, CancellationToken cancellationToken = default) =>
        GetAtOrBeforeAsync(season, DateTimeOffset.MaxValue, cancellationToken);

    public async Task<FantasyPriceSnapshot?> GetAtOrBeforeAsync(int season, DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        var directory = SeasonDirectory(season);
        if (!Directory.Exists(directory)) return null;
        foreach (var path in Directory.EnumerateFiles(directory, "snapshot-*.json").OrderDescending(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(path);
                var snapshot = await JsonSerializer.DeserializeAsync<FantasyPriceSnapshot>(stream, JsonOptions, cancellationToken);
                if (snapshot is not null && snapshot.Season == season && snapshot.Riders is { Count: > 0 } && snapshot.FetchedAtUtc <= cutoff)
                    return snapshot;
            }
            catch (JsonException exception) { _logger.LogWarning(exception, "Ignoring a corrupt Fantasy price snapshot {FileName}", Path.GetFileName(path)); }
        }
        return null;
    }

    public async Task<FantasyPriceStatus> GetStatusAsync(int season, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetLatestAsync(season, cancellationToken);
        var statusPath = Path.Combine(SeasonDirectory(season), "sync-status.json");
        SyncStatus? status = null;
        if (File.Exists(statusPath))
        {
            try
            {
                await using var stream = File.OpenRead(statusPath);
                status = await JsonSerializer.DeserializeAsync<SyncStatus>(stream, JsonOptions, cancellationToken);
            }
            catch (JsonException exception) { _logger.LogWarning(exception, "Fantasy sync status is corrupt"); }
        }
        return new(snapshot, snapshot is null || _time.GetUtcNow() - snapshot.FetchedAtUtc > TimeSpan.FromHours(Math.Max(1, _options.StaleAfterHours)),
            status?.Error, status?.AttemptedAtUtc);
    }

    public async Task SaveAsync(FantasyPriceSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var directory = SeasonDirectory(snapshot.Season);
        Directory.CreateDirectory(directory);
        var name = $"snapshot-{snapshot.FetchedAtUtc.UtcDateTime:yyyyMMddTHHmmssfffffff}-{Guid.NewGuid():N}.json";
        await WriteAtomicAsync(Path.Combine(directory, name), snapshot, overwrite: false, cancellationToken);
        await RecordAttemptAsync(snapshot.Season, null, cancellationToken);
    }

    public async Task RecordAttemptAsync(int season, string? error, CancellationToken cancellationToken = default)
    {
        var directory = SeasonDirectory(season);
        Directory.CreateDirectory(directory);
        await WriteAtomicAsync(Path.Combine(directory, "sync-status.json"), new SyncStatus(_time.GetUtcNow(), error), overwrite: true, cancellationToken);
    }

    private string SeasonDirectory(int season) => season is >= 2000 and <= 2200
        ? Path.Combine(_directory, season.ToString(System.Globalization.CultureInfo.InvariantCulture))
        : throw new ArgumentOutOfRangeException(nameof(season));

    private static async Task WriteAtomicAsync<T>(string path, T value, bool overwrite, CancellationToken cancellationToken)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record SyncStatus(DateTimeOffset AttemptedAtUtc, string? Error);
}
