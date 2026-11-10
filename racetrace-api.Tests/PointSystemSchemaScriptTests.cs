namespace RaceTrace.Api.Tests;

public sealed class PointSystemSchemaScriptTests
{
    private static readonly string Script = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "point-system-schema.sql")
    );

    [Fact]
    public void CompetitionLevelConstraintsArePresent()
    {
        Assert.Contains(
            "competition_levels_code_key unique (code)",
            Script,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "competition_levels_priority_key unique (priority)",
            Script,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "competition_levels_priority_positive check (priority > 0)",
            Script,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void PointSystemAndRuleConstraintsArePresent()
    {
        Assert.Contains("points_systems_valid_seasons_check", Script, StringComparison.Ordinal);
        Assert.Contains("point_system_stages_system_session_key", Script, StringComparison.Ordinal);
        Assert.Contains("point_rules_position_from_check", Script, StringComparison.Ordinal);
        Assert.Contains("point_rules_position_to_check", Script, StringComparison.Ordinal);
        Assert.Contains("point_rules_points_check", Script, StringComparison.Ordinal);
    }

    [Fact]
    public void RiderImageAndResultPointDiagnosticsArePresent()
    {
        Assert.Contains("profile_image_url", Script, StringComparison.Ordinal);
        Assert.Contains(
            "length(profile_image_url) between 1 and 2048",
            Script,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "add column if not exists point_rule_id bigint",
            Script,
            StringComparison.Ordinal
        );
        Assert.Contains("points_awarded", Script, StringComparison.Ordinal);
    }
}
