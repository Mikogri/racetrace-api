using RaceTrace.Api.Features.Metrics;
using Microsoft.AspNetCore.Http;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Services;

public sealed class RiderSimilarityService
{
    private const int MaximumProfiles = 300;

    private readonly DomainQueryService _domainQueries;
    private readonly PaceService _pace;
    private readonly QueryValidationService _validator;

    public RiderSimilarityService(
        DomainQueryService domainQueries,
        PaceService pace,
        QueryValidationService validator)
    {
        _domainQueries = domainQueries;
        _pace = pace;
        _validator = validator;
    }

    public async Task<RiderSimilarityResponseDto> GetAsync(
        IQueryCollection query,
        CancellationToken cancellationToken)
    {
        var options = ParseOptions(query);
        var context = await _domainQueries.ParseMetricsContextAsync(query, _validator, cancellationToken);
        var matchingSessions = await _domainQueries.GetMatchingSessionsAsync(
            context,
            cancellationToken: cancellationToken);
        if (matchingSessions.Count == 0)
        {
            return Empty(context, options);
        }

        var categoriesBySessionId = matchingSessions
            .Where(session => !string.IsNullOrWhiteSpace(session.String("id")))
            .ToDictionary(
                session => session.String("id")!,
                session => session.Object("categories")?.String("code"),
                StringComparer.OrdinalIgnoreCase);
        var preaggregated = await _domainQueries.GetRankingPreaggregatesAsync(
            matchingSessions,
            cancellationToken: cancellationToken);

        RankingDataBundle bundle;
        IReadOnlyList<RiderSectorObservation> observations;
        string dataSource;
        if (preaggregated is not null)
        {
            bundle = DomainQueryService.RestrictToCategories(preaggregated.Data, context);
            observations = _pace.CalculateSimilarityObservations(
                preaggregated.PaceSessions,
                categoriesBySessionId);
            observations = RestrictObservationCategories(observations, context);
            dataSource = "preaggregated";
        }
        else
        {
            bundle = await _domainQueries.GetRankingDataAsync(
                context,
                resolvedSessionIds: matchingSessions
                    .Select(session => session.String("id") ?? string.Empty),
                cancellationToken: cancellationToken);
            observations = _pace.CalculateSimilarityObservations(bundle, categoriesBySessionId);
            dataSource = "ranking_data";
        }

        var grouped = observations
            .GroupBy(
                observation => $"{observation.RiderId}\u001f{observation.CategoryCode}",
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var duplicateRiderIds = grouped
            .Select(group => group.First())
            .GroupBy(item => item.RiderId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var profiles = grouped
            .Select(group =>
            {
                var first = group.First();
                var profileId = duplicateRiderIds.Contains(first.RiderId)
                    ? $"{first.RiderId}:{first.CategoryCode ?? "unknown"}"
                    : first.RiderId;
                return RiderProfileBuilder.Build(
                    profileId,
                    first.RiderId,
                    first.CategoryCode,
                    group.ToArray(),
                    options.MinSectors,
                    options.MinSessions);
            })
            .Where(profile => profile is not null)
            .Select(profile => profile!)
            .OrderBy(profile => profile.ProfileId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (profiles.Length > MaximumProfiles)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "TOO_MANY_RIDERS",
                $"The selected data produces {profiles.Length} rider profiles. Narrow the filters to at most {MaximumProfiles} profiles.");
        }

        var analysis = RiderSimilarityCalculator.Analyze(
            profiles,
            options.MinSharedFeatures,
            options.ClusterCount,
            allowCrossCategory: false);
        var riderRows = bundle.Results
            .Where(result => !string.IsNullOrWhiteSpace(result.String("rider_id")))
            .GroupBy(result => result.String("rider_id")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var riders = profiles.Select(profile =>
        {
            riderRows.TryGetValue(profile.RiderId, out var row);
            var rider = row?.Object("riders");
            var name = string.Join(" ", new[]
            {
                rider?.String("first_name"),
                rider?.String("last_name")
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
            return new RiderSimilarityRiderDto(
                profile.ProfileId,
                profile.RiderId,
                string.IsNullOrWhiteSpace(name) ? $"Rider {profile.RiderId}" : name,
                rider?.String("nation"),
                PublicApiCodes.ToPublicCategory(profile.CategoryCode),
                profile.SectorCount,
                profile.SessionCount,
                profile.Evidence,
                profile.Profile,
                analysis.ClusterByProfileId.GetValueOrDefault(profile.ProfileId),
                analysis.TagsByProfileId.GetValueOrDefault(profile.ProfileId, ["Balanced"]));
        }).ToArray();

        return new RiderSimilarityResponseDto(
            BuildFilters(context, options),
            riders,
            analysis.Pairs,
            analysis.Clusters,
            analysis.Merges,
            new RiderSimilarityMetaDto(
                riders.Length,
                profiles.Sum(profile => profile.SectorCount),
                observations.Select(item => item.SessionId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                RiderSimilarityCalculator.FeatureNames,
                dataSource));
    }

    private SimilarityOptions ParseOptions(IQueryCollection query)
    {
        var minSectors = _validator.IntAny(query, "minSectors", "min_sectors") ?? 8;
        var minSessions = _validator.IntAny(query, "minSessions", "min_sessions") ?? 2;
        var minShared = _validator.IntAny(query, "minSharedFeatures", "min_shared_features") ?? 4;
        var clusterCount = _validator.IntAny(query, "clusterCount", "cluster_count");
        if (minSectors is < 1 or > 10000)
            throw _validator.BadQuery("minSectors must be between 1 and 10000.");
        if (minSessions is < 1 or > 1000)
            throw _validator.BadQuery("minSessions must be between 1 and 1000.");
        if (minShared is < 1 or > 7)
            throw _validator.BadQuery("minSharedFeatures must be between 1 and 7.");
        if (clusterCount is < 1 or > 50)
            throw _validator.BadQuery("clusterCount must be between 1 and 50.");
        return new SimilarityOptions(minSectors, minSessions, minShared, clusterCount);
    }

    private static IReadOnlyList<RiderSectorObservation> RestrictObservationCategories(
        IReadOnlyList<RiderSectorObservation> observations,
        RiderMetricsContext context)
    {
        if (context.CategoryCodes.Count == 0)
            return observations;
        var requested = context.CategoryCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return observations.Where(item => item.CategoryCode is not null && requested.Contains(item.CategoryCode)).ToArray();
    }

    private static RiderSimilarityFiltersDto BuildFilters(RiderMetricsContext context, SimilarityOptions options) =>
        new(
            context.CategoryCodes.Select(PublicApiCodes.ToPublicCategory).Where(value => value is not null).Select(value => value!).ToArray(),
            context.TrackIds,
            context.ConditionNames.Count > 0
                ? context.ConditionNames
                : context.ConditionIds.Select(id => id.ToString()).ToArray(),
            options.MinSectors,
            options.MinSessions,
            options.MinSharedFeatures,
            options.ClusterCount);

    private static RiderSimilarityResponseDto Empty(RiderMetricsContext context, SimilarityOptions options) =>
        new(
            BuildFilters(context, options),
            [],
            [],
            [],
            [],
            new RiderSimilarityMetaDto(0, 0, 0, RiderSimilarityCalculator.FeatureNames, "none"));

    private sealed record SimilarityOptions(
        int MinSectors,
        int MinSessions,
        int MinSharedFeatures,
        int? ClusterCount);
}
