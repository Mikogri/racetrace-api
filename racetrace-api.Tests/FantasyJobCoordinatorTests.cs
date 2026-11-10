using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RaceTrace.Api.Features.Fantasy;
using RaceTrace.Api.Infrastructure;

namespace RaceTrace.Api.Tests;

public sealed class FantasyJobCoordinatorTests
{
    [Fact]
    public async Task DisabledModeKeepsLocalPublicationWithoutRpc()
    {
        var rpc = new JobRpc();
        var coordinator = Create(rpc, false);
        string? local = null;
        var result = await coordinator.RunAsync(
            "prices:2026",
            "cycle",
            "prices",
            "2026",
            _ => Task.FromResult("value"),
            (value, _) =>
            {
                local = value;
                return Task.CompletedTask;
            },
            CancellationToken.None
        );
        Assert.Equal("value", result);
        Assert.Equal("value", local);
        Assert.Empty(rpc.Calls);
    }

    [Fact]
    public async Task ConflictDoesNotFetchOrPublish()
    {
        var rpc = new JobRpc { Acquired = false };
        var fetched = false;
        await Assert.ThrowsAsync<FantasyJobConflictException>(() =>
            Create(rpc)
                .RunAsync(
                    "prices:2026",
                    "cycle",
                    "prices",
                    "2026",
                    _ =>
                    {
                        fetched = true;
                        return Task.FromResult("value");
                    },
                    (_, _) => throw new Exception("Local publication is forbidden"),
                    CancellationToken.None
                )
        );
        Assert.False(fetched);
        Assert.DoesNotContain("fantasy_job_publish", rpc.Calls);
    }

    [Fact]
    public async Task CompletedCycleReturnsOriginalResultWithoutRepeatingWork()
    {
        var rpc = new JobRpc { CompletedPayload = JsonValue.Create("original") };
        var result = await Create(rpc)
            .RunAsync(
                "prices:2026",
                "cycle",
                "prices",
                "2026",
                _ => throw new Exception("Completed work must not repeat"),
                (string _, CancellationToken _) =>
                    throw new Exception("Local publication is forbidden"),
                CancellationToken.None
            );
        Assert.Equal("original", result);
        Assert.Single(rpc.Calls);
    }

    [Fact]
    public async Task StaleOwnerCannotTreatRejectedPublicationAsSuccess()
    {
        var rpc = new JobRpc { Publish = false };
        await Assert.ThrowsAsync<FantasyLeaseLostException>(() =>
            Create(rpc)
                .RunAsync(
                    "prices:2026",
                    "cycle",
                    "prices",
                    "2026",
                    _ => Task.FromResult("value"),
                    (_, _) => throw new Exception("Local publication is forbidden"),
                    CancellationToken.None
                )
        );
        Assert.Equal(17, rpc.Publication!["p_lease_token"]!.GetValue<long>());
        Assert.Equal("cycle", rpc.Publication["p_run_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task LostHeartbeatCancelsWorkAndNeverPublishes()
    {
        var rpc = new JobRpc { Renew = false };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create(rpc)
                .RunAsync(
                    "prices:2026",
                    "cycle",
                    "prices",
                    "2026",
                    async token =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), token);
                        return "value";
                    },
                    (_, _) => throw new Exception("Local publication is forbidden"),
                    CancellationToken.None
                )
        );
        Assert.Contains("fantasy_job_renew", rpc.Calls);
        Assert.DoesNotContain("fantasy_job_publish", rpc.Calls);
    }

    [Fact]
    public async Task FailedWorkNeverPublishesAndReleasesItsLease()
    {
        var rpc = new JobRpc();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Create(rpc)
                .RunAsync(
                    "prices:2026",
                    "cycle",
                    "prices",
                    "2026",
                    _ => throw new InvalidDataException("invalid input"),
                    (string _, CancellationToken _) => Task.CompletedTask,
                    CancellationToken.None
                )
        );
        Assert.Contains("fantasy_job_fail", rpc.Calls);
        Assert.DoesNotContain("fantasy_job_publish", rpc.Calls);
    }

    [Fact]
    public void ScheduledCycleIsUtcAlignedNotProcessStartupRelative()
    {
        var start = DateTimeOffset.Parse("2026-10-10T12:01:00Z");
        Assert.Equal(
            FantasyJobCoordinator.ScheduledRunId(start, TimeSpan.FromHours(6)),
            FantasyJobCoordinator.ScheduledRunId(start.AddMinutes(50), TimeSpan.FromHours(6))
        );
        Assert.NotEqual(
            FantasyJobCoordinator.ScheduledRunId(start, TimeSpan.FromHours(6)),
            FantasyJobCoordinator.ScheduledRunId(start.AddHours(6), TimeSpan.FromHours(6))
        );
    }

    private static FantasyJobCoordinator Create(JobRpc rpc, bool enabled = true) =>
        new(
            rpc,
            Options.Create(
                new FantasyOptions
                {
                    DistributedCoordinationEnabled = enabled,
                    LeaseDuration = TimeSpan.FromSeconds(10),
                    HeartbeatInterval = TimeSpan.FromMilliseconds(30),
                }
            ),
            TimeProvider.System,
            NullLogger<FantasyJobCoordinator>.Instance
        );

    private sealed class JobRpc : ISupabaseRestClient
    {
        public bool Acquired { get; init; } = true;
        public bool Publish { get; init; } = true;
        public bool Renew { get; init; } = true;
        public JsonNode? CompletedPayload { get; init; }
        public List<string> Calls { get; } = [];
        public JsonObject? Publication { get; private set; }

        public Task<JsonNode?> RpcAsync(
            string functionName,
            JsonObject payload,
            SupabaseRole role,
            CancellationToken cancellationToken = default
        )
        {
            Assert.Equal(SupabaseRole.Service, role);
            Calls.Add(functionName);
            if (functionName == "fantasy_job_acquire")
                return Task.FromResult<JsonNode?>(
                    CompletedPayload is null
                        ? new JsonObject { ["acquired"] = Acquired, ["leaseToken"] = 17L }
                        : new JsonObject
                        {
                            ["completed"] = true,
                            ["payload"] = CompletedPayload.DeepClone(),
                        }
                );
            if (functionName == "fantasy_job_publish")
            {
                Publication = payload;
                return Task.FromResult<JsonNode?>(JsonValue.Create(Publish));
            }
            return Task.FromResult<JsonNode?>(
                JsonValue.Create(functionName != "fantasy_job_renew" || Renew)
            );
        }

        public Task<SupabaseQueryResult> GetAsync(
            SupabaseQuery query,
            bool count = false,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<JsonObject?> GetSingleAsync(
            SupabaseQuery query,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<JsonObject>> PostAsync(
            string table,
            JsonNode payload,
            SupabaseRole role,
            string? onConflict = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<JsonObject>> PatchAsync(
            string table,
            JsonObject payload,
            SupabaseQuery query,
            SupabaseRole role,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task DeleteAsync(
            string table,
            SupabaseQuery query,
            SupabaseRole role,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }
}
