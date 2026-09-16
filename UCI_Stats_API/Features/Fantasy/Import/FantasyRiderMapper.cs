using System.Globalization;
using System.Text;

namespace UCI_Stats_API.Features.Fantasy.Import;

public static class FantasyRiderMapper
{
    public static IReadOnlyList<PinkbikeFantasyRider> Map(
        IReadOnlyList<PinkbikeFantasyRider> athletes,
        IReadOnlyList<FantasyRiderIdentity> identities,
        FantasyImportOptions options,
        FantasyPriceSnapshot? previous = null)
    {
        var byId = identities.ToDictionary(x => x.RiderId, StringComparer.Ordinal);
        var knownMappings = previous?.Riders.Where(x => x.RiderId != null)
            .ToDictionary(x => x.ExternalId, x => x.RiderId!, StringComparer.Ordinal) ?? [];
        var byName = identities.SelectMany(identity => new[] { identity.Name }
                .Concat(options.NameAliases.GetValueOrDefault(identity.RiderId) ?? [])
                .Select(name => (Key: NormalizeName(name), Identity: identity)))
            .GroupBy(x => x.Key).ToDictionary(x => x.Key, x => x.Select(y => y.Identity).DistinctBy(y => y.RiderId).ToArray());

        var mapped = athletes.Select(athlete =>
        {
            var manual = options.RiderMappings.TryGetValue(athlete.ExternalId, out var targetId);
            if (!manual) knownMappings.TryGetValue(athlete.ExternalId, out targetId);
            if (targetId != null)
            {
                if (!byId.TryGetValue(targetId, out var identity) || !GenderMatches(athlete, identity))
                    return athlete with { RiderId = null, MappingStatus = "invalid_mapping" };
                return athlete with { RiderId = targetId, MappingStatus = manual ? "manual" : "external_id" };
            }

            var matches = byName.GetValueOrDefault(NormalizeName(athlete.Name))?.Where(x => GenderMatches(athlete, x)).ToArray() ?? [];
            return matches.Length == 1
                ? athlete with { RiderId = matches[0].RiderId, MappingStatus = "normalized_name" }
                : athlete with { RiderId = null, MappingStatus = matches.Length > 1 ? "ambiguous" : "unmapped" };
        }).ToArray();

        // Two external athletes must never silently represent the same internal rider.
        var duplicates = mapped.Where(x => x.RiderId != null).GroupBy(x => x.RiderId)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        return mapped.Select(x => x.RiderId != null && duplicates.Contains(x.RiderId)
            ? x with { RiderId = null, MappingStatus = "duplicate_mapping" } : x).ToArray();
    }

    public static string NormalizeName(string name)
    {
        var decomposed = name.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToUpperInvariant(character));
        }
        return builder.ToString();
    }

    private static bool GenderMatches(PinkbikeFantasyRider athlete, FantasyRiderIdentity identity) =>
        string.IsNullOrEmpty(identity.Gender) || string.Equals(athlete.Gender, identity.Gender, StringComparison.OrdinalIgnoreCase);
}
