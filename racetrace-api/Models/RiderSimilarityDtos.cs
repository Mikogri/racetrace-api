using System.Text.Json.Serialization;

namespace RaceTrace.Api.Models;

public sealed record RiderSimilarityResponseDto(
    RiderSimilarityFiltersDto Filters,
    IReadOnlyList<RiderSimilarityRiderDto> Riders,
    [property: JsonPropertyName("similarityMatrix")] IReadOnlyList<RiderSimilarityPairDto> SimilarityMatrix,
    IReadOnlyList<RiderSimilarityClusterDto> Clusters,
    IReadOnlyList<RiderSimilarityMergeDto> Dendrogram,
    RiderSimilarityMetaDto Meta);

public sealed record RiderSimilarityFiltersDto(
    IReadOnlyList<string> Categories,
    [property: JsonPropertyName("trackIds")] IReadOnlyList<string> TrackIds,
    IReadOnlyList<string> Conditions,
    [property: JsonPropertyName("minSectors")] int MinSectors,
    [property: JsonPropertyName("minSessions")] int MinSessions,
    [property: JsonPropertyName("minSharedFeatures")] int MinSharedFeatures,
    [property: JsonPropertyName("clusterCount")] int? ClusterCount);

public sealed record RiderSimilarityRiderDto(
    [property: JsonPropertyName("profileId")] string ProfileId,
    [property: JsonPropertyName("riderId")] string RiderId,
    string Name,
    [property: JsonPropertyName("countryCode")] string? CountryCode,
    string? Category,
    [property: JsonPropertyName("sectorCount")] int SectorCount,
    [property: JsonPropertyName("sessionCount")] int SessionCount,
    double Evidence,
    RiderSectorProfileDto Profile,
    [property: JsonPropertyName("clusterId")] int ClusterId,
    [property: JsonPropertyName("styleTags")] IReadOnlyList<string> StyleTags);

public sealed record RiderSectorProfileDto(
    double? Start,
    double? Middle,
    double? Finish,
    double? Technical,
    double? Fast,
    double? Consistency,
    double? Peak,
    double? Weak,
    double? Variance);

public sealed record RiderSimilarityPairDto(
    [property: JsonPropertyName("riderAId")] string RiderAId,
    [property: JsonPropertyName("riderBId")] string RiderBId,
    double? Similarity,
    [property: JsonPropertyName("sharedFeatures")] int SharedFeatures);

public sealed record RiderSimilarityClusterDto(
    [property: JsonPropertyName("clusterId")] int ClusterId,
    [property: JsonPropertyName("riderIds")] IReadOnlyList<string> RiderIds,
    int Size,
    RiderSectorProfileDto Centroid,
    IReadOnlyList<string> Tags);

public sealed record RiderSimilarityMergeDto(
    [property: JsonPropertyName("nodeId")] string NodeId,
    [property: JsonPropertyName("leftId")] string LeftId,
    [property: JsonPropertyName("rightId")] string RightId,
    double Distance,
    int Size);

public sealed record RiderSimilarityMetaDto(
    [property: JsonPropertyName("riderCount")] int RiderCount,
    [property: JsonPropertyName("sectorObservationCount")] int SectorObservationCount,
    [property: JsonPropertyName("sessionCount")] int SessionCount,
    [property: JsonPropertyName("featureNames")] IReadOnlyList<string> FeatureNames,
    [property: JsonPropertyName("dataSource")] string DataSource);
