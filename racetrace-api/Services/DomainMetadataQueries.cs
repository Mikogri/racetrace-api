using System.Text.Json.Nodes;
using RaceTrace.Api.Features.Metadata;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Infrastructure.Supabase;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Services;

internal sealed class DomainMetadataQueries(
    ISupabaseRestClient supabase,
    DisplayMetadataService metadata
)
{
    private readonly ISupabaseRestClient _supabase = supabase;
    private readonly DisplayMetadataService _metadata = metadata;
    private Task<DisplayMetadataDto>? _metadataTask;
    private readonly Dictionary<string, Task<JsonObject?>> _categoryReads = new(
        StringComparer.Ordinal
    );

    public async Task<string> ResolveCategoryCodeAsync(
        string value,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return PublicApiCodes.ToDatabaseCategory(value);
        }
        catch (ArgumentException)
        {
            var metadata = await GetMetadataAsync(cancellationToken);
            return FindDatabaseCode(metadata.Categories, value)
                ?? throw new ArgumentException($"Unsupported category '{value}'.", nameof(value));
        }
    }

    public async Task<string> ResolveSessionTypeCodeAsync(
        string value,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return PublicApiCodes.ToDatabaseSessionType(value);
        }
        catch (ArgumentException)
        {
            var metadata = await GetMetadataAsync(cancellationToken);
            return FindDatabaseCode(metadata.SessionTypes, value)
                ?? throw new ArgumentException(
                    $"Unsupported session type '{value}'.",
                    nameof(value)
                );
        }
    }

    // This service is request-scoped: multiple dynamic filters share one metadata snapshot.
    private Task<DisplayMetadataDto> GetMetadataAsync(CancellationToken cancellationToken) =>
        _metadataTask ??= _metadata.GetAsync(cancellationToken);

    private static string? FindDatabaseCode(IEnumerable<DisplayLabelDto> entries, string value)
    {
        var normalized = value.Trim();
        return entries
            .FirstOrDefault(entry =>
                string.Equals(entry.Code, normalized, StringComparison.OrdinalIgnoreCase)
                || entry.Aliases.Contains(normalized, StringComparer.OrdinalIgnoreCase)
            )
            ?.DatabaseCode;
    }

    public async Task<string?> FindCategoryIdByCodeAsync(
        string? categoryCode,
        SupabaseRole role = SupabaseRole.Anon,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(categoryCode))
        {
            return null;
        }

        var normalizedCode = categoryCode.Trim();
        try
        {
            normalizedCode = PublicApiCodes.ToDatabaseCategory(normalizedCode);
        }
        catch (ArgumentException)
        {
            // Admin-managed categories outside the public DHI set keep their database code.
        }

        Task<JsonObject?> ReadCategory() =>
            _supabase.GetSingleAsync(
                new SupabaseQuery("categories")
                    .Select("id,code")
                    .Filter("code", "eq", normalizedCode),
                role,
                cancellationToken
            );

        // Share identical public reads within this request; privileged imports stay fresh.
        Task<JsonObject?> pending;
        if (role != SupabaseRole.Anon)
            pending = ReadCategory();
        else
            lock (_categoryReads)
            {
                if (!_categoryReads.TryGetValue(normalizedCode, out pending!))
                    _categoryReads[normalizedCode] = pending = ReadCategory();
            }
        var category = await pending.WaitAsync(cancellationToken);

        return category?.String("id");
    }

    internal async Task<string?> FindLegacyCompetitionClassIdByCodeAsync(
        string? code,
        SupabaseRole role = SupabaseRole.Anon,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var competitionClass = await _supabase.GetSingleAsync(
            new SupabaseQuery("competition_ranks").Select("id,code").Filter("code", "eq", code),
            role,
            cancellationToken
        );

        return competitionClass?.String("id");
    }

    public async Task<string?> FindCompetitionClassIdByCodeAsync(
        string? code,
        SupabaseRole role = SupabaseRole.Anon,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var competitionClass = await _supabase.GetSingleAsync(
            new SupabaseQuery("competition_levels")
                .Select("id,code,is_active")
                .Filter("code", "eq", code.Trim().ToLowerInvariant()),
            role,
            cancellationToken
        );

        return competitionClass?.String("id");
    }
}
