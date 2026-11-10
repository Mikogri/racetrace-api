using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Infrastructure.Telemetry;

namespace RaceTrace.Api.Features.Fantasy;

public sealed class FantasyJobConflictException()
    : InvalidOperationException("Another worker owns this Fantasy job.");

public sealed class FantasyLeaseLostException()
    : OperationCanceledException("The Fantasy job lease was lost; its result was not published.");

internal sealed record FantasyJobStatus(
    string Status,
    DateTimeOffset LastAttemptAt,
    DateTimeOffset? LastSuccessAt
);

/// <summary>Opt-in shared publication; the database fences the result and completes its run in one transaction.</summary>
public sealed class FantasyJobCoordinator(
    ISupabaseRestClient database,
    IOptions<FantasyOptions> options,
    TimeProvider time,
    ILogger<FantasyJobCoordinator> logger
)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public bool Enabled => options.Value.DistributedCoordinationEnabled;

    public static string ScheduledRunId(DateTimeOffset now, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));
        return "scheduled:"
            + (now.UtcTicks / interval.Ticks).ToString(CultureInfo.InvariantCulture);
    }

    public async Task<T> RunAsync<T>(
        string jobKey,
        string runId,
        string kind,
        string artifactKey,
        Func<CancellationToken, Task<T>> work,
        Func<T, CancellationToken, Task> localPublish,
        CancellationToken token
    )
    {
        var jobName = kind == "forecast" ? "fantasy_prepare" : "fantasy_sync";
        if (!Enabled)
        {
            var localStarted = Stopwatch.GetTimestamp();
            try
            {
                var local = await work(token);
                token.ThrowIfCancellationRequested();
                await localPublish(local, token);
                ApiTelemetry.Job(jobName, "success", Stopwatch.GetElapsedTime(localStarted));
                return local;
            }
            catch (Exception error)
            {
                ApiTelemetry.Job(
                    jobName,
                    error is OperationCanceledException ? "cancelled" : "failure",
                    Stopwatch.GetElapsedTime(localStarted)
                );
                throw;
            }
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (runId.Length > 200)
            throw new ArgumentException(
                "Fantasy run IDs must be at most 200 characters.",
                nameof(runId)
            );
        var owner = Guid.NewGuid().ToString("N");
        var identity = new JsonObject
        {
            ["p_job_key"] = jobKey,
            ["p_run_id"] = runId,
            ["p_owner_id"] = owner,
        };
        var acquire = identity.DeepClone().AsObject();
        acquire["p_lease_seconds"] = (int)options.Value.LeaseDuration.TotalSeconds;
        var acquired = await database.RpcAsync(
            "fantasy_job_acquire",
            acquire,
            SupabaseRole.Service,
            token
        );
        if (acquired?["completed"]?.GetValue<bool>() == true)
        {
            ApiTelemetry.Job(jobName, "skipped");
            return acquired["payload"].Deserialize<T>(Json)
                ?? throw new InvalidDataException("The completed Fantasy result is missing.");
        }
        if (acquired?["acquired"]?.GetValue<bool>() != true)
        {
            ApiTelemetry.Job(jobName, "lease_conflict");
            throw new FantasyJobConflictException();
        }
        if (acquired["attempt"]?.GetValue<int>() > 1)
            ApiTelemetry.JobRetry(jobName);
        identity["p_lease_token"] = acquired["leaseToken"]!.GetValue<long>();
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var heartbeatStop = new CancellationTokenSource();
        var heartbeat = HeartbeatAsync(identity, execution, heartbeatStop.Token);
        var started = Stopwatch.GetTimestamp();
        var published = false;
        try
        {
            var result = await work(execution.Token);
            execution.Token.ThrowIfCancellationRequested();
            var payload = JsonSerializer.SerializeToNode(result, Json)!;
            var publication = identity.DeepClone().AsObject();
            publication["p_kind"] = kind;
            publication["p_artifact_key"] = artifactKey;
            publication["p_payload"] = payload;
            publication["p_computed_at"] = payload is JsonObject obj
                ? (obj["fetchedAtUtc"] ?? obj["preparedAtUtc"])?.DeepClone()
                    ?? JsonValue.Create(time.GetUtcNow())
                : JsonValue.Create(time.GetUtcNow());
            published =
                (
                    await database.RpcAsync(
                        "fantasy_job_publish",
                        publication,
                        SupabaseRole.Service,
                        execution.Token
                    )
                )?.GetValue<bool>() == true;
            if (!published)
                throw new FantasyLeaseLostException();
            ApiTelemetry.Job(jobName, "success", Stopwatch.GetElapsedTime(started));
            logger.LogInformation(
                "Fantasy job {JobKind} succeeded in {DurationMs} ms",
                kind,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds
            );
            return result;
        }
        catch (Exception error)
        {
            var outcome =
                error is FantasyLeaseLostException
                || execution.IsCancellationRequested && !token.IsCancellationRequested
                    ? "lease_loss"
                : error is OperationCanceledException ? "cancelled"
                : "failure";
            ApiTelemetry.Job(jobName, outcome, Stopwatch.GetElapsedTime(started));
            throw;
        }
        finally
        {
            await heartbeatStop.CancelAsync();
            await heartbeat;
            if (!published)
            {
                // Best effort: a lost response may have committed. The RPC never rewrites successful or newer runs.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    await database.RpcAsync(
                        "fantasy_job_fail",
                        identity,
                        SupabaseRole.Service,
                        cleanup.Token
                    );
                }
                catch (Exception error)
                {
                    logger.LogWarning(
                        "Fantasy job cleanup failed ({ErrorType}); lease expiry permits retry",
                        error.GetType().Name
                    );
                }
            }
        }
    }

    private async Task HeartbeatAsync(
        JsonObject identity,
        CancellationTokenSource execution,
        CancellationToken stopping
    )
    {
        try
        {
            while (true)
            {
                await Task.Delay(options.Value.HeartbeatInterval, time, stopping);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                timeout.CancelAfter(options.Value.HeartbeatInterval);
                var renewal = identity.DeepClone().AsObject();
                renewal["p_lease_seconds"] = (int)options.Value.LeaseDuration.TotalSeconds;
                if (
                    (
                        await database.RpcAsync(
                            "fantasy_job_renew",
                            renewal,
                            SupabaseRole.Service,
                            timeout.Token
                        )
                    )?.GetValue<bool>() != true
                )
                    throw new FantasyLeaseLostException();
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception error)
        {
            logger.LogWarning(
                "Fantasy lease heartbeat failed ({ErrorType}); cancelling worker",
                error.GetType().Name
            );
            await execution.CancelAsync();
        }
    }

    public async Task<T?> ReadAsync<T>(
        string kind,
        string key,
        DateTimeOffset cutoff,
        CancellationToken token
    )
        where T : class
    {
        var result = await database.RpcAsync(
            "fantasy_artifact_read",
            new JsonObject
            {
                ["p_kind"] = kind,
                ["p_artifact_key"] = key,
                ["p_cutoff"] = cutoff == DateTimeOffset.MaxValue ? null : JsonValue.Create(cutoff),
            },
            SupabaseRole.Service,
            token
        );
        return result?.Deserialize<T>(Json);
    }

    internal async Task<FantasyJobStatus?> ReadStatusAsync(string key, CancellationToken token) =>
        (
            await database.RpcAsync(
                "fantasy_job_status",
                new JsonObject { ["p_job_key"] = key },
                SupabaseRole.Service,
                token
            )
        )?.Deserialize<FantasyJobStatus>(Json);
}
