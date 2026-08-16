using System.Text.Json;
using System.Text.Json.Nodes;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Models;

namespace UCI_Stats_API.Services;

public interface IPointSystemSeeder
{
    Task<PointSystemSeedResult> SeedAsync(Stream json, CancellationToken cancellationToken = default);
}

public sealed class PointSystemSeeder : IPointSystemSeeder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly ISupabaseRestClient _supabase;

    public PointSystemSeeder(ISupabaseRestClient supabase)
    {
        _supabase = supabase;
    }

    public async Task<PointSystemSeedResult> SeedAsync(Stream json, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(json);
        var parsed = await JsonSerializer.DeserializeAsync<PointSystemSeedDocument>(json, JsonOptions, cancellationToken)
            ?? throw new PointSystemSeedValidationException(["Point system seed JSON is empty."]);
        var document = PointSystemSeedValidator.ValidateAndNormalize(parsed);

        var stagesProcessed = 0;
        var rulesWritten = 0;
        foreach (var definition in document.PointSystems)
        {
            var competitionClassId = await RequiredReferenceIdAsync(
                "competition_levels",
                definition.CompetitionClassCode,
                cancellationToken);
            var categoryId = await RequiredReferenceIdAsync(
                "categories",
                definition.CategoryCode,
                cancellationToken);

            var savedSystem = (await _supabase.PostAsync(
                "points_systems",
                PointSystemPayload(definition, competitionClassId, categoryId),
                SupabaseRole.Service,
                onConflict: "code",
                cancellationToken)).Single();
            var pointSystemId = RequiredId(savedSystem, "points_systems");

            foreach (var stage in definition.Stages)
            {
                var savedStage = (await _supabase.PostAsync(
                    "point_system_stages",
                    new JsonObject
                    {
                        ["point_system_id"] = pointSystemId,
                        ["session_type"] = stage.SessionType,
                        ["awards_points"] = stage.AwardsPoints,
                        ["awards_points_on_season_final_round"] = stage.AwardsPointsOnSeasonFinalRound
                    },
                    SupabaseRole.Service,
                    onConflict: "point_system_id,session_type",
                    cancellationToken)).Single();
                var stageId = RequiredId(savedStage, "point_system_stages");

                await _supabase.DeleteAsync(
                    "point_rules",
                    new SupabaseQuery("point_rules").Filter("point_system_stage_id", "eq", stageId),
                    SupabaseRole.Service,
                    cancellationToken);

                if (stage.Rules.Count > 0)
                {
                    var rules = new JsonArray(stage.Rules.Select(rule => (JsonNode)new JsonObject
                    {
                        ["point_system_stage_id"] = stageId,
                        ["position_from"] = rule.PositionFrom,
                        ["position_to"] = rule.PositionTo,
                        ["points"] = rule.Points,
                        ["season_final_points"] = rule.SeasonFinalPoints
                    }).ToArray());
                    await _supabase.PostAsync(
                        "point_rules",
                        rules,
                        SupabaseRole.Service,
                        cancellationToken: cancellationToken);
                    rulesWritten += rules.Count;
                }

                stagesProcessed++;
            }
        }

        return new PointSystemSeedResult(document.PointSystems.Count, stagesProcessed, rulesWritten);
    }

    private async Task<string> RequiredReferenceIdAsync(
        string table,
        string code,
        CancellationToken cancellationToken)
    {
        var row = await _supabase.GetSingleAsync(
            new SupabaseQuery(table)
                .Select("id,code")
                .Filter("code", "eq", code),
            SupabaseRole.Service,
            cancellationToken);

        return row?.String("id")
            ?? throw new PointSystemSeedValidationException([$"{table} does not contain code '{code}'."]);
    }

    private static JsonObject PointSystemPayload(
        PointSystemSeedDefinition definition,
        string competitionClassId,
        string categoryId)
    {
        return new JsonObject
        {
            ["code"] = definition.Code,
            ["name"] = definition.Name,
            ["competition_level_id"] = competitionClassId,
            ["discipline"] = definition.Discipline,
            ["category_id"] = categoryId,
            ["valid_from_season"] = definition.ValidFromSeason,
            ["valid_to_season"] = definition.ValidToSeason,
            ["source_url"] = definition.SourceUrl,
            ["notes"] = definition.Notes
        };
    }

    private static string RequiredId(JsonObject row, string table)
    {
        return row.String("id")
            ?? throw new InvalidDataException($"Supabase did not return an id after upserting {table}.");
    }
}
