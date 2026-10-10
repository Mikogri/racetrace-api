using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace RaceTrace.Api.Features.Fantasy;

public sealed record FantasyPreparedRider(string RiderId, string Name, string? CountryCode, string Gender,
    decimal Confidence, decimal? PredictedPosition, decimal ExpectedFinalPoints, decimal ExpectedQ1Bonus, decimal ExpectedFantasyPoints);

public sealed record FantasyRoundBacktest(int Year, string RoundId, string Category, FantasyBacktestMetrics Metrics);

public sealed record FantasyPreparedRound(int Season, string RoundId, DateTimeOffset RoundStartUtc, DateTimeOffset PreparedAtUtc,
    string EngineVersion, string ProjectionVersion, string RulesVersion, string EventFingerprint,
    IReadOnlyList<FantasyPreparedRider> Riders, IReadOnlyList<string> Warnings, IReadOnlyList<FantasyRoundBacktest> Backtests,
    IReadOnlyDictionary<string, FantasyStageCalibration>? Calibrations = null,
    string? DataRevision = null,
    IReadOnlyList<FantasyTrainingField>? TrainingFields = null,
    string? CalculationFingerprint = null,
    string? ConfigurationHash = null,
    DateTimeOffset? TrainingDataCutoff = null,
    DateTimeOffset? PredictionAsOf = null,
    int? SimulationSeed = null,
    int? SimulationIterations = null);

/// <summary>Prepared forecasts survive restarts; a failed refresh never replaces the last successful file.</summary>
public sealed class FantasyPreparedStore(IOptions<FantasyOptions> options, IHostEnvironment environment, FantasyJobCoordinator? jobs = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _directory = Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.Value.DataDirectory, "predictions"));

    public async Task<FantasyPreparedRound?> ReadAsync(string roundId, CancellationToken token = default)
    {
        if (jobs?.Enabled == true) return await jobs.ReadAsync<FantasyPreparedRound>("forecast", roundId, DateTimeOffset.MaxValue, token);
        var path = FilePath(roundId);
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, true);
        var prepared = await JsonSerializer.DeserializeAsync<FantasyPreparedRound>(stream, Json, token)
            ?? throw new InvalidDataException("The stored Fantasy forecast is empty.");
        if (prepared.RoundId != roundId)
            throw new InvalidDataException("The stored Fantasy forecast does not match the requested round.");
        return prepared;
    }

    public async Task WriteAsync(FantasyPreparedRound prepared, CancellationToken token = default)
    {
        if (jobs?.Enabled == true) throw new InvalidOperationException("Distributed Fantasy forecasts require fenced job publication.");
        var destination = FilePath(prepared.RoundId);
        Directory.CreateDirectory(_directory);
        var temporary = Path.Combine(_directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, prepared, Json, token);
                await stream.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private string FilePath(string roundId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roundId);
        // Round IDs are untrusted route data, never path segments.
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(roundId)));
        return Path.Combine(_directory, name + ".json");
    }
}
