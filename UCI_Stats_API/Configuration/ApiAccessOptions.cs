namespace UCI_Stats_API.Configuration;

public sealed class ApiAccessOptions
{
    public const string SectionName = "ApiAccess";

    public string? ReadSecret { get; set; }
}
