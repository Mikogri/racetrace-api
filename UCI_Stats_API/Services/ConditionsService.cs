using System.Text.Json.Nodes;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Models;

namespace UCI_Stats_API.Services;

public sealed class ConditionsService
{
    private static readonly Dictionary<string, string> ConditionSorts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = "id",
        ["name"] = "name",
        ["is_dry"] = "is_dry"
    };

    private readonly ISupabaseRestClient _supabase;
    private readonly QueryValidationService _validator;

    public ConditionsService(ISupabaseRestClient supabase, QueryValidationService validator)
    {
        _supabase = supabase;
        _validator = validator;
    }

    public async Task<ListResponse<JsonObject>> ListConditionsAsync(IQueryCollection queryString, CancellationToken cancellationToken)
    {
        var page = _validator.Page(queryString);
        var sort = _validator.Sort(queryString, ConditionSorts, "name", "asc");
        var query = new SupabaseQuery("conditions")
            .Select("*")
            .Limit(page.Limit)
            .Offset(page.Offset)
            .Order(ConditionSorts[sort.Sort], sort.Descending);

        query.Filter("is_dry", "eq", _validator.Bool(queryString, "is_dry"));

        var q = _validator.String(queryString, "q");
        if (!string.IsNullOrWhiteSpace(q))
        {
            query.Filter("name", "ilike", $"*{q.Replace("*", string.Empty, StringComparison.Ordinal)}*");
        }

        var response = await _supabase.GetAsync(query, count: true, cancellationToken: cancellationToken);
        return ResponseFactory.Page(response.Data, page.Page, page.Limit, response.Total, sort.Sort, sort.Order);
    }
}
