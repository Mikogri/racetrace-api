using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure;

namespace RaceTrace.Api.Features.Imports.ChronoRace;

internal sealed class ChronoRaceBatchStore(ISupabaseRestClient supabase)
{
    private const int LookupBatchSize = 100;
    private const int WriteBatchSize = 500;
    private readonly ISupabaseRestClient _supabase = supabase;

    internal async Task<IReadOnlyList<JsonObject>> GetRowsByValuesAsync(
        string table,
        string select,
        string column,
        IEnumerable<string> values,
        CancellationToken cancellationToken
    )
    {
        var rows = new List<JsonObject>();
        foreach (
            var batch in values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Chunk(LookupBatchSize)
        )
        {
            var response = await _supabase.GetAsync(
                new SupabaseQuery(table).Select(select).In(column, batch),
                role: SupabaseRole.Service,
                cancellationToken: cancellationToken
            );
            rows.AddRange(response.Data);
        }

        return rows;
    }

    internal async Task<IReadOnlyList<JsonObject>> PostRowsAsync(
        string table,
        IReadOnlyCollection<JsonObject> rows,
        string? onConflict,
        CancellationToken cancellationToken
    )
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var savedRows = new List<JsonObject>();
        foreach (var batch in rows.Chunk(WriteBatchSize))
        {
            var payload = new JsonArray(batch.Select(row => (JsonNode)row.DeepClone()).ToArray());
            var saved = await _supabase.PostAsync(
                table,
                payload,
                SupabaseRole.Service,
                onConflict,
                cancellationToken
            );
            savedRows.AddRange(saved);
        }

        return savedRows;
    }

    internal async Task DeleteRowsByValuesAsync(
        string table,
        string column,
        IEnumerable<string> values,
        CancellationToken cancellationToken
    )
    {
        foreach (
            var batch in values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Chunk(LookupBatchSize)
        )
        {
            await _supabase.DeleteAsync(
                table,
                new SupabaseQuery(table).In(column, batch),
                SupabaseRole.Service,
                cancellationToken
            );
        }
    }
}
