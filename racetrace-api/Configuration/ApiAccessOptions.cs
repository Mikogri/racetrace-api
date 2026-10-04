namespace RaceTrace.Api.Configuration;

public sealed class ApiAccessOptions
{
    public const string SectionName = "ApiAccess";

    public string? ReadSecret { get; set; }
}
