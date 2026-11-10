namespace RaceTrace.Api.Features.Fantasy;

public sealed class FantasyOptions
{
    public const string SectionName = "Fantasy";
    public string DataDirectory { get; set; } = "App_Data/Fantasy";
    public bool ScheduledSyncEnabled { get; set; }
    public bool DistributedCoordinationEnabled { get; set; }
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan SyncInterval { get; set; } = TimeSpan.FromHours(6);
    public TimeSpan LiveSyncInterval { get; set; } = TimeSpan.FromMinutes(15);

    // An entry list is event data, never inferred from Fantasy prices or the eventual final.
    public Dictionary<string, FantasyEventOptions> Events { get; set; } = new();
}

public sealed class FantasyEventOptions
{
    public bool? Q1BonusApplies { get; set; }
    public List<string>? StartListRiderIds { get; set; }
    public string? StartListSourceUrl { get; set; }
    public DateTimeOffset? StartListFetchedAtUtc { get; set; }
    public bool StartListIsComplete { get; set; }
}

public sealed class FantasyWorkCoordinator
{
    internal SemaphoreSlim PrepareGate { get; } = new(1, 1);
}
