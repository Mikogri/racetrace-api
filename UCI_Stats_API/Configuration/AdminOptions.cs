namespace UCI_Stats_API.Configuration;

public sealed class AdminOptions
{
    public const string SectionName = "Admin";

    public string? ApiSecret { get; set; }
}
