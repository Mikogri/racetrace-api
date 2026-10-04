namespace RaceTrace.Api.Models;

public sealed record PointSystemSeedDocument
{
    public IReadOnlyList<PointSystemSeedDefinition> PointSystems { get; init; } = [];
}

public sealed record PointSystemSeedDefinition
{
    public string Code { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string CompetitionClassCode { get; init; } = string.Empty;

    public string Discipline { get; init; } = "DHI";

    public string CategoryCode { get; init; } = string.Empty;

    public int ValidFromSeason { get; init; }

    public int ValidToSeason { get; init; }

    public string? SourceUrl { get; init; }

    public string? Notes { get; init; }

    public IReadOnlyList<PointSystemStageSeedDefinition> Stages { get; init; } = [];
}

public sealed record PointSystemStageSeedDefinition
{
    public string SessionType { get; init; } = string.Empty;

    public bool AwardsPoints { get; init; } = true;

    public bool AwardsPointsOnSeasonFinalRound { get; init; } = true;

    public IReadOnlyList<PointRuleSeedDefinition> Rules { get; init; } = [];
}

public sealed record PointRuleSeedDefinition
{
    public int PositionFrom { get; init; }

    public int? PositionTo { get; init; }

    public int Points { get; init; }

    public int? SeasonFinalPoints { get; init; }
}

public sealed record PointSystemSeedResult(
    int SystemsProcessed,
    int StagesProcessed,
    int RulesWritten);
