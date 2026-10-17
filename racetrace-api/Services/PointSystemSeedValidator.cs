using RaceTrace.Api.Models;

namespace RaceTrace.Api.Services;

public static class PointSystemSeedValidator
{
    public static PointSystemSeedDocument ValidateAndNormalize(PointSystemSeedDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var errors = new List<string>();
        var systems = new List<PointSystemSeedDefinition>(document.PointSystems.Count);
        var systemCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var systemIndex = 0; systemIndex < document.PointSystems.Count; systemIndex++)
        {
            var source = document.PointSystems[systemIndex];
            var path = $"point_systems[{systemIndex}]";
            var code = NormalizeLowerCode(source.Code);
            var name = source.Name.Trim();
            var competitionClassCode = NormalizeLowerCode(source.CompetitionClassCode);
            var discipline = source.Discipline.Trim().ToUpperInvariant();
            var categoryCode = NormalizeCategoryCode(source.CategoryCode);

            Required(code, $"{path}.code", errors);
            Required(name, $"{path}.name", errors);
            Required(competitionClassCode, $"{path}.competition_class_code", errors);
            Required(discipline, $"{path}.discipline", errors);
            Required(categoryCode, $"{path}.category_code", errors);

            if (!string.IsNullOrEmpty(code) && !systemCodes.Add(code))
            {
                errors.Add($"{path}.code duplicates point system code '{code}'.");
            }

            if (source.ValidFromSeason is < 1990 or > 2100 ||
                source.ValidToSeason is < 1990 or > 2100 ||
                source.ValidFromSeason > source.ValidToSeason)
            {
                errors.Add($"{path} has an invalid season range.");
            }

            if (source.SourceUrl is not null &&
                (!Uri.TryCreate(source.SourceUrl, UriKind.Absolute, out var sourceUri) ||
                 sourceUri.Scheme is not ("http" or "https")))
            {
                errors.Add($"{path}.source_url must be an absolute HTTP or HTTPS URL.");
            }

            var stages = NormalizeStages(source.Stages, path, errors);
            systems.Add(source with
            {
                Code = code,
                Name = name,
                CompetitionClassCode = competitionClassCode,
                Discipline = discipline,
                CategoryCode = categoryCode,
                SourceUrl = source.SourceUrl?.Trim(),
                Notes = NullIfWhiteSpace(source.Notes),
                Stages = stages
            });
        }

        if (errors.Count > 0)
        {
            throw new PointSystemSeedValidationException(errors);
        }

        return new PointSystemSeedDocument { PointSystems = systems };
    }

    private static string NormalizeCategoryCode(string value)
    {
        var normalized = value.Trim();
        try
        {
            return PublicApiCodes.ToDatabaseCategory(normalized);
        }
        catch (ArgumentException)
        {
            return normalized.ToUpperInvariant();
        }
    }

    private static List<PointSystemStageSeedDefinition> NormalizeStages(
        IReadOnlyList<PointSystemStageSeedDefinition> sourceStages,
        string systemPath,
        List<string> errors)
    {
        var stages = new List<PointSystemStageSeedDefinition>(sourceStages.Count);
        var sessionTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var stageIndex = 0; stageIndex < sourceStages.Count; stageIndex++)
        {
            var source = sourceStages[stageIndex];
            var path = $"{systemPath}.stages[{stageIndex}]";
            var sessionType = string.IsNullOrWhiteSpace(source.SessionType)
                ? string.Empty
                : DomainQueryService.NormalizeSessionType(source.SessionType);

            Required(sessionType, $"{path}.session_type", errors);
            if (!string.IsNullOrEmpty(sessionType) && !sessionTypes.Add(sessionType))
            {
                errors.Add($"{path}.session_type duplicates stage '{sessionType}'.");
            }

            var rules = source.Rules
                .OrderBy(rule => rule.PositionFrom)
                .ThenBy(rule => rule.PositionTo ?? int.MaxValue)
                .ToArray();

            for (var ruleIndex = 0; ruleIndex < rules.Length; ruleIndex++)
            {
                var rule = rules[ruleIndex];
                var rulePath = $"{path}.rules[{ruleIndex}]";
                if (rule.PositionFrom < 1)
                {
                    errors.Add($"{rulePath}.position_from must be at least 1.");
                }

                if (rule.PositionTo is not null && rule.PositionTo < rule.PositionFrom)
                {
                    errors.Add($"{rulePath}.position_to must be null or at least position_from.");
                }

                if (rule.Points < 0)
                {
                    errors.Add($"{rulePath}.points cannot be negative.");
                }

                if (rule.SeasonFinalPoints is < 0)
                {
                    errors.Add($"{rulePath}.season_final_points cannot be negative.");
                }

                if (ruleIndex > 0)
                {
                    var previous = rules[ruleIndex - 1];
                    if (previous.PositionTo is null || rule.PositionFrom <= previous.PositionTo)
                    {
                        errors.Add($"{rulePath} overlaps the previous position range.");
                    }
                }
            }

            stages.Add(source with
            {
                SessionType = sessionType,
                Rules = rules
            });
        }

        return stages;
    }

    private static string NormalizeLowerCode(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void Required(string value, string path, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{path} is required.");
        }
    }
}

public sealed class PointSystemSeedValidationException : Exception
{
    public PointSystemSeedValidationException(IReadOnlyList<string> errors)
        : base(string.Join(Environment.NewLine, errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}
