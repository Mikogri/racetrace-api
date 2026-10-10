namespace RaceTrace.Api.Infrastructure.Telemetry;

public sealed class TelemetryOptions
{
    public bool Enabled { get; set; } = true;
    public bool Detailed { get; set; } = true;
    public bool LogRequests { get; set; }
    public double TraceSampleRatio { get; set; } = 0.1;
    public string? OtlpEndpoint { get; set; }
}
