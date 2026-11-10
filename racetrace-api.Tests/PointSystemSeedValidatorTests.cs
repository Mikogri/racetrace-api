using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class PointSystemSeedValidatorTests
{
    [Fact]
    public void ValidDefinitionIsNormalized()
    {
        var document = Document(stages: [Stage("qualifying", Rule(1, 1, 50), Rule(2, 10, 25))]);

        var normalized = PointSystemSeedValidator.ValidateAndNormalize(document);

        var system = Assert.Single(normalized.PointSystems);
        Assert.Equal("world_cup_em_2026", system.Code);
        Assert.Equal("world_cup", system.CompetitionClassCode);
        Assert.Equal("DHI", system.Discipline);
        Assert.Equal("EM", system.CategoryCode);
        Assert.Equal("QUALI", Assert.Single(system.Stages).SessionType);
    }

    [Fact]
    public void InvalidSeasonRangeIsRejected()
    {
        var document = Document(validFrom: 2026, validTo: 2025);

        var exception = Assert.Throws<PointSystemSeedValidationException>(() =>
            PointSystemSeedValidator.ValidateAndNormalize(document)
        );

        Assert.Contains(
            exception.Errors,
            error => error.Contains("season range", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void InvalidPositionRangeAndNegativePointsAreRejected()
    {
        var document = Document(stages: [Stage("FINAL", Rule(5, 4, -1))]);

        var exception = Assert.Throws<PointSystemSeedValidationException>(() =>
            PointSystemSeedValidator.ValidateAndNormalize(document)
        );

        Assert.Contains(
            exception.Errors,
            error => error.Contains("position_to", StringComparison.Ordinal)
        );
        Assert.Contains(
            exception.Errors,
            error => error.Contains("negative", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void DuplicateStagesAreRejectedAfterNormalization()
    {
        var document = Document(stages: [Stage("Final"), Stage("FINALS")]);

        var exception = Assert.Throws<PointSystemSeedValidationException>(() =>
            PointSystemSeedValidator.ValidateAndNormalize(document)
        );

        Assert.Contains(
            exception.Errors,
            error => error.Contains("duplicates stage 'FINAL'", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void OverlappingPointRangesAreRejected()
    {
        var document = Document(stages: [Stage("FINAL", Rule(1, 10, 100), Rule(10, 20, 50))]);

        var exception = Assert.Throws<PointSystemSeedValidationException>(() =>
            PointSystemSeedValidator.ValidateAndNormalize(document)
        );

        Assert.Contains(
            exception.Errors,
            error => error.Contains("overlaps", StringComparison.Ordinal)
        );
    }

    private static PointSystemSeedDocument Document(
        int validFrom = 2026,
        int validTo = 2026,
        IReadOnlyList<PointSystemStageSeedDefinition>? stages = null
    )
    {
        return new PointSystemSeedDocument
        {
            PointSystems =
            [
                new PointSystemSeedDefinition
                {
                    Code = " World_Cup_EM_2026 ",
                    Name = "World Cup Elite Men 2026",
                    CompetitionClassCode = " World_Cup ",
                    Discipline = "dhi",
                    CategoryCode = "me",
                    ValidFromSeason = validFrom,
                    ValidToSeason = validTo,
                    Stages = stages ?? [],
                },
            ],
        };
    }

    private static PointSystemStageSeedDefinition Stage(
        string type,
        params PointRuleSeedDefinition[] rules
    )
    {
        return new PointSystemStageSeedDefinition { SessionType = type, Rules = rules };
    }

    private static PointRuleSeedDefinition Rule(int from, int? to, int points) =>
        new()
        {
            PositionFrom = from,
            PositionTo = to,
            Points = points,
        };
}
