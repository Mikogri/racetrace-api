namespace RaceTrace.Api.Services;

public static class PublicApiCodes
{
    private static readonly Dictionary<string, string> PublicToDatabaseCategories = new Dictionary<
        string,
        string
    >(StringComparer.OrdinalIgnoreCase)
    {
        ["elite_men"] = "EM",
        ["elite_women"] = "EW",
        ["junior_men"] = "JM",
        ["junior_women"] = "JW",
        ["Elite Men"] = "EM",
        ["Elite Women"] = "EW",
        ["Junior Men"] = "JM",
        ["Junior Women"] = "JW",
        ["EM"] = "EM",
        ["EW"] = "EW",
        ["JM"] = "JM",
        ["JW"] = "JW",
        ["men_elite"] = "EM",
        ["women_elite"] = "EW",
        ["men_junior"] = "JM",
        ["women_junior"] = "JW",
        ["Men Elite"] = "EM",
        ["Women Elite"] = "EW",
        ["Men Junior"] = "JM",
        ["Women Junior"] = "JW",
        ["ME"] = "EM",
        ["WE"] = "EW",
        ["MJ"] = "JM",
        ["WJ"] = "JW",
    };

    private static readonly IReadOnlyDictionary<string, string> PublicToDatabaseSessions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["seeding"] = "SEEDING",
            ["qualifying"] = "QUALI",
            ["qualifying_1"] = "Q1",
            ["qualifying_2"] = "Q2",
            ["semi_final"] = "SEMI",
            ["final"] = "FINAL",
            ["timed_training"] = "TIMED_TRAINING",
            ["training"] = "PRACTICE",
            ["QUALI"] = "QUALI",
            ["Q1"] = "Q1",
            ["Q2"] = "Q2",
            ["SEMI"] = "SEMI",
            ["TIMED_TRAINING"] = "TIMED_TRAINING",
            ["PRACTICE"] = "PRACTICE",
        };

    public static IReadOnlySet<string> PublicCategories { get; } =
        new HashSet<string>(
            ["elite_men", "elite_women", "junior_men", "junior_women"],
            StringComparer.OrdinalIgnoreCase
        );

    public static IReadOnlySet<string> PublicSessionTypes { get; } =
        new HashSet<string>(
            [
                "seeding",
                "qualifying",
                "qualifying_1",
                "qualifying_2",
                "semi_final",
                "final",
                "timed_training",
                "training",
            ],
            StringComparer.OrdinalIgnoreCase
        );

    public static string ToDatabaseCategory(string value)
    {
        return PublicToDatabaseCategories.TryGetValue(value.Trim(), out var code)
            ? code
            : throw new ArgumentException($"Unsupported category '{value}'.", nameof(value));
    }

    public static string? ToPublicCategory(string? value)
    {
        return value?.Trim().ToUpperInvariant() switch
        {
            "EM" or "ME" => "elite_men",
            "EW" or "WE" => "elite_women",
            "JM" or "MJ" => "junior_men",
            "JW" or "WJ" => "junior_women",
            _ => value?.Trim().ToLowerInvariant(),
        };
    }

    public static string ToDatabaseSessionType(string value)
    {
        return PublicToDatabaseSessions.TryGetValue(value.Trim(), out var code)
            ? code
            : throw new ArgumentException($"Unsupported session type '{value}'.", nameof(value));
    }

    public static string? ToPublicSessionType(string? value)
    {
        return value?.Trim().ToUpperInvariant() switch
        {
            "SEEDING" => "seeding",
            "QUALI" or "QUALIFICATION" or "QUALIFYING" => "qualifying",
            "Q1" or "QUALIFYING_1" or "QUALIFYING1" => "qualifying_1",
            "Q2" or "QUALIFYING_2" or "QUALIFYING2" => "qualifying_2",
            "SEMI" or "SEMIFINAL" or "SEMI_FINAL" or "SEMI-FINAL" => "semi_final",
            "FINAL" or "FINALS" => "final",
            "TIMED_TRAINING" or "TIMEDTRAINING" => "timed_training",
            "PRACTICE" or "TRAINING" => "training",
            _ => value?.Trim().ToLowerInvariant(),
        };
    }

    public static bool IsQualification(string? value)
    {
        return ToPublicSessionType(value)
            is "qualifying"
                or "qualifying_1"
                or "qualifying_2"
                or "semi_final";
    }

    public static bool IsFinal(string? value) => ToPublicSessionType(value) == "final";

    public static IEnumerable<string> GetCategoryAliases(string databaseCode) =>
        PublicToDatabaseCategories
            .Where(pair => ToPublicCategory(pair.Value) == ToPublicCategory(databaseCode))
            .Select(pair => pair.Key);

    public static IEnumerable<string> GetSessionTypeAliases(string databaseCode) =>
        PublicToDatabaseSessions
            .Keys.Concat([
                "QUALIFICATION",
                "QUALIFYING",
                "QUALIFYING_1",
                "QUALIFYING1",
                "QUALIFYING_2",
                "QUALIFYING2",
                "SEMIFINAL",
                "SEMI_FINAL",
                "SEMI-FINAL",
                "FINALS",
                "TIMEDTRAINING",
                "TRAINING",
            ])
            .Where(alias => ToPublicSessionType(alias) == ToPublicSessionType(databaseCode));
}
