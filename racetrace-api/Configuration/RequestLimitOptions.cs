namespace RaceTrace.Api.Configuration;

public sealed class RequestLimitOptions
{
    public int RequestsPerMinute { get; set; } = 240;
    public int AdminRequestsPerMinute { get; set; } = 12;
    public int HeavyConcurrency { get; set; } = 8;
}
