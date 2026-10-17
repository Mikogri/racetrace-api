using RaceTrace.Api.Features.Metadata;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Infrastructure.Supabase;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Services;

internal sealed class DomainMetadataQueries(ISupabaseRestClient supabase, DisplayMetadataService metadata)
{
    private readonly ISupabaseRestClient _supabase = supabase;
    private readonly DisplayMetadataService _metadata = metadata;
    private Task<DisplayMetadataDto>? _metadataTask;

    public async Task<string> ResolveCategoryCodeAsync(string value, CancellationToken cancellationToken)
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

    public async Task<string> ResolveSessionTypeCodeAsync(string value, CancellationToken cancellationToken)
    {
        try
        {
            return PublicApiCodes.ToDatabaseSessionType(value);
        }
        catch (ArgumentException)
        {
            var metadata = await GetMetadataAsync(cancellationToken);
            return FindDatabaseCode(metadata.SessionTypes, value)
                ?? throw new ArgumentException($"Unsupported session type '{value}'.", nameof(value));
        }
    }

    // This service is request-scoped: multiple dynamic filters share one metadata snapshot.
    private Task<DisplayMetadataDto> GetMetadataAsync(CancellationToken cancellationToken) =>
        _metadataTask ??= _metadata.GetAsync(cancellationToken);

    private static string? FindDatabaseCode(IEnumerable<DisplayLabelDto> entries, string value)
    {
        var normalized = value.Trim();
        return entries.FirstOrDefault(entry =>
            string.Equals(entry.Code, normalized, StringComparison.OrdinalIgnoreCase) ||
            entry.Aliases.Contains(normalized, StringComparer.OrdinalIgnoreCase))?.DatabaseCode;
    }

    public async Task<string?> FindCategoryIdByCodeAsync(string? categoryCode, SupabaseRole role = SupabaseRole.Anon, CancellationToken cancellationToken = default)
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

        var category = await _supabase.GetSingleAsync(
            new SupabaseQuery("categories")
                .Select("id,code")
                .Filter("code", "eq", normalizedCode),
            role,
            cancellationToken);

        return category?.String("id");
    }

    internal async Task<string?> FindLegacyCompetitionClassIdByCodeAsync(string? code, SupabaseRole role = SupabaseRole.Anon, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var competitionClass = await _supabase.GetSingleAsync(
            new SupabaseQuery("competition_ranks")
                .Select("id,code")
                .Filter("code", "eq", code),
            role,
            cancellationToken);

        return competitionClass?.String("id");
    }

    public async Task<string?> FindCompetitionClassIdByCodeAsync(string? code, SupabaseRole role = SupabaseRole.Anon, CancellationToken cancellationToken = default)
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
            cancellationToken);

        return competitionClass?.String("id");
    }

}
