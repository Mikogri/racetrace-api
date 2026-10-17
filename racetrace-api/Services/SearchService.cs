using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure;

namespace RaceTrace.Api.Services;

public sealed class SearchService
{
    private static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "all",
        "riders",
        "rounds",
        "sessions",
        "results",
        "tracks",
        "teams"
    };

    private readonly ISupabaseRestClient _supabase;
    private readonly QueryValidationService _validator;
    private readonly DomainQueryService _domainQueries;

    public SearchService(ISupabaseRestClient supabase, QueryValidationService validator, DomainQueryService domainQueries)
    {
        _supabase = supabase;
        _validator = validator;
        _domainQueries = domainQueries;
    }

    public async Task<JsonObject> SearchAsync(IQueryCollection queryString, CancellationToken cancellationToken)
    {
        var q = _validator.String(queryString, "q");
        var type = _validator.String(queryString, "type") ?? "all";
        var limit = Math.Min(_validator.Int(queryString, "limit") ?? 10, 100);

        if (!AllowedTypes.Contains(type))
        {
            throw _validator.BadQuery($"Invalid type '{type}'. Allowed values: {string.Join(", ", AllowedTypes)}.");
        }

        if (string.IsNullOrWhiteSpace(q))
        {
            return new JsonObject
            {
                ["riders"] = new JsonArray(),
                ["rounds"] = new JsonArray(),
                ["sessions"] = new JsonArray(),
                ["results"] = new JsonArray(),
                ["tracks"] = new JsonArray(),
                ["teams"] = new JsonArray()
            };
        }

        var safe = q.Replace("*", string.Empty, StringComparison.Ordinal);
        var response = new JsonObject();

        if (type is "all" or "riders")
        {
            var riderExpressions = new List<string>
            {
                $"first_name.ilike.*{safe}*",
                $"last_name.ilike.*{safe}*",
                $"uci_code.ilike.*{safe}*"
            };
            if (long.TryParse(safe, out var riderNumericQuery))
            {
                riderExpressions.Add($"id.eq.{riderNumericQuery}");
                riderExpressions.Add($"uci_id.eq.{riderNumericQuery}");
            }

            var riders = (await _supabase.GetAsync(new SupabaseQuery("riders")
                .Select("*")
                .Or(string.Join(",", riderExpressions))
                .Limit(limit), cancellationToken: cancellationToken)).Data;
            await DecorateRidersWithTeamsAsync(riders, cancellationToken);
            response["riders"] = ToArray(riders);
        }

        if (type is "all" or "rounds")
        {
            var rounds = (await _supabase.GetAsync(new SupabaseQuery("rounds")
                .Select("*,tracks(*),competition_levels(*),conditions(*)")
                .Or($"context_name.ilike.*{safe}*")
                .Add("order", "start_date.desc.nullslast,id.desc")
                .Limit(limit), cancellationToken: cancellationToken)).Data;
            foreach (var round in rounds)
            {
                DomainQueryService.NormalizeRoundEmbed(round);
            }

            response["rounds"] = ToArray(rounds);
        }

        if (type is "all" or "sessions")
        {
            var sessions = await _supabase.GetAsync(new SupabaseQuery("sessions")
                .Select(DomainQueryService.SessionSelect)
                .Or($"name.ilike.*{safe}*,session_type.ilike.*{safe}*,status.ilike.*{safe}*")
                .Limit(limit), cancellationToken: cancellationToken);
            DomainQueryService.NormalizeSessionEmbeds(sessions.Data);
            response["sessions"] = ToArray(sessions.Data);
        }

        if (type is "all" or "results")
        {
            var resultQuery = new SupabaseQuery("results")
                .Select(DomainQueryService.ResultSelect)
                .Limit(limit);
            if (int.TryParse(safe, out var raceNr))
            {
                resultQuery.Filter("race_nr", "eq", raceNr);
            }
            else
            {
                var riderIds = await _domainQueries.FindRiderIdsAsync(safe, null, cancellationToken: cancellationToken);
                if (riderIds.Count == 0)
                {
                    resultQuery.Filter("status", "ilike", $"*{safe}*");
                }
                else
                {
                    resultQuery.In("rider_id", riderIds);
                }
            }

            var results = (await _supabase.GetAsync(resultQuery, cancellationToken: cancellationToken)).Data;
            DomainQueryService.NormalizeResultEmbeds(results);
            await _domainQueries.DecorateMissingResultTeamsFromHistoryAsync(
                results,
                cancellationToken);
            response["results"] = ToArray(results);
        }

        if (type is "all" or "tracks")
        {
            response["tracks"] = ToArray((await _supabase.GetAsync(new SupabaseQuery("tracks")
                .Select("*")
                .Or($"name.ilike.*{safe}*,venue.ilike.*{safe}*,country_code.ilike.*{safe}*")
                .Limit(limit), cancellationToken: cancellationToken)).Data);
        }

        if (type is "all" or "teams")
        {
            response["teams"] = ToArray((await _supabase.GetAsync(new SupabaseQuery("teams")
                .Select("*")
                .Or($"name.ilike.*{safe}*,code.ilike.*{safe}*")
                .Limit(limit), cancellationToken: cancellationToken)).Data);
        }

        return response;
    }

    private async Task DecorateRidersWithTeamsAsync(IReadOnlyList<JsonObject> riders, CancellationToken cancellationToken)
    {
        var riderIds = riders
            .Select(rider => rider.String("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToArray();
        if (riderIds.Length == 0)
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var history = await _domainQueries.GetRiderTeamHistoryAsync(
            riderIds,
            toDate: today,
            cancellationToken: cancellationToken);
        foreach (var rider in riders)
        {
            var riderId = rider.String("id") ?? string.Empty;
            var team = DomainQueryService.FindTeamPeriod(history, riderId, today)?
                .Object("teams");
            rider["teams"] = team is null
                ? []
                : new JsonArray(team.DeepClone());
        }
    }

    private static JsonArray ToArray(IEnumerable<JsonObject> rows)
    {
        return [.. rows.Select(row => row.DeepClone()).ToArray()];
    }
}
