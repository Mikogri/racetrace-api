using System.Text.Json.Nodes;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Features.Results;

// Normalize database/provider embeds at the read boundary, outside the calculators.
internal static class ResultEmbedMapping
{
    public static string NormalizeSessionType(string sessionType)
    {
        var normalized = sessionType.Trim().ToUpperInvariant();
        return normalized switch
        {
            "FINAL" or "FINALS" => "FINAL",
            "QUALIFICATION" or "QUALIFYING" => "QUALI",
            "SEMIFINAL" or "SEMI_FINAL" or "SEMI-FINAL" => "SEMI",
            _ => normalized,
        };
    }

    public static bool IsFinalResult(JsonObject result)
    {
        var sessionType = result.Object("sessions")?.String("session_type");
        return !string.IsNullOrWhiteSpace(sessionType)
            && string.Equals(
                NormalizeSessionType(sessionType),
                "FINAL",
                StringComparison.OrdinalIgnoreCase
            );
    }

    public static bool IsFinalSession(JsonObject session)
    {
        var sessionType = session.String("session_type");
        return !string.IsNullOrWhiteSpace(sessionType)
            && string.Equals(
                NormalizeSessionType(sessionType),
                "FINAL",
                StringComparison.OrdinalIgnoreCase
            );
    }

    public static void NormalizeResultEmbeds(IReadOnlyList<JsonObject> results)
    {
        foreach (var result in results)
        {
            var pointRows = result.Array("result_points")?.OfType<JsonObject>().ToArray() ?? [];
            result["awarded_points"] = pointRows.Sum(row => row.Decimal("points_awarded") ?? 0m);
            if (pointRows.Length == 1)
            {
                result["applied_point_system_id"] = pointRows[0]["points_system_id"]?.DeepClone();
                result["applied_point_system_stage_id"] = pointRows[0]
                    ["point_system_stage_id"]
                    ?.DeepClone();
                result["applied_point_rule_id"] = pointRows[0]["point_rule_id"]?.DeepClone();
            }

            if (result.Object("sessions") is { } session)
            {
                NormalizeSessionEmbed(session);
            }
        }
    }

    public static void NormalizeSessionEmbeds(IReadOnlyList<JsonObject> sessions)
    {
        foreach (var session in sessions)
        {
            NormalizeSessionEmbed(session);
        }
    }

    public static void NormalizeSessionEmbed(JsonObject session)
    {
        var roundCategory = session.Object("round_categories");
        if (roundCategory is null)
        {
            return;
        }

        if (roundCategory.Object("rounds") is { } round)
        {
            NormalizeRoundEmbed(round);
            session["rounds"] = round.DeepClone();
        }

        if (roundCategory.Object("categories") is { } category)
        {
            session["categories"] = category.DeepClone();
        }

        if (
            session.Object("conditions") is null
            && roundCategory.Object("rounds")?.Object("conditions") is { } condition
        )
        {
            session["conditions"] = condition.DeepClone();
        }
    }

    public static void NormalizeRoundEmbed(JsonObject round)
    {
        var competitionClassId = round.String("competition_level_id");
        if (round.Object("competition_levels") is not { } competitionClass)
        {
            round["competition_class_id"] = competitionClassId;
            round["competition_class"] = null;
            round["competition_class_priority"] = null;
        }
        else
        {
            round["competition_class_id"] = competitionClassId ?? competitionClass.String("id");
            round["competition_class"] = competitionClass.DeepClone();
            round["competition_class_priority"] = competitionClass.Int("priority");
        }

        round.Remove("competition_level_id");
        round.Remove("competition_levels");
        round.Remove("competition_rank_id");
        round.Remove("competition_ranks");
    }
}
