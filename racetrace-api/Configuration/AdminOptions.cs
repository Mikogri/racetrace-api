namespace RaceTrace.Api.Configuration;

public sealed class AdminOptions
{
    public const string SectionName = "Admin";

    public string? ApiSecret { get; set; }
}
