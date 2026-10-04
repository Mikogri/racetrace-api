using System.Text.Json.Serialization;

namespace RaceTrace.Api.Models;

public sealed record RiderSimilarityResponseDto(
    [property: JsonPropertyName("filters")] RiderSimilarityFiltersDto Filters,
    [property: JsonPropertyName("riders")] IReadOnlyList<RiderSimilarityRiderDto> Riders,
    [property: JsonPropertyName("similarityMatrix")] IReadOnlyList<RiderSimilarityPairDto> SimilarityMatrix,
    [property: JsonPropertyName("clusters")] IReadOnlyList<RiderSimilarityClusterDto> Clusters,
    [property: JsonPropertyName("dendrogram")] IReadOnlyList<RiderSimilarityMergeDto> Dendrogram,
    [property: JsonPropertyName("meta")] RiderSimilarityMetaDto Meta);

public sealed record RiderSimilarityFiltersDto(
    [property: JsonPropertyName("categories")] IReadOnlyList<string> Categories,
    [property: JsonPropertyName("trackIds")] IReadOnlyList<string> TrackIds,
    [property: JsonPropertyName("conditions")] IReadOnlyList<string> Conditions,
    [property: JsonPropertyName("minSectors")] int MinSectors,
    [property: JsonPropertyName("minSessions")] int MinSessions,
    [property: JsonPropertyName("minSharedFeatures")] int MinSharedFeatures,
    [property: JsonPropertyName("clusterCount")] int? ClusterCount);

public sealed record RiderSimilarityRiderDto(
    [property: JsonPropertyName("profileId")] string ProfileId,
    [property: JsonPropertyName("riderId")] string RiderId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("countryCode")] string? CountryCode,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("sectorCount")] int SectorCount,
    [property: JsonPropertyName("sessionCount")] int SessionCount,
    [property: JsonPropertyName("evidence")] double Evidence,
    [property: JsonPropertyName("profile")] RiderSectorProfileDto Profile,
    [property: JsonPropertyName("clusterId")] int ClusterId,
    [property: JsonPropertyName("styleTags")] IReadOnlyList<string> StyleTags);

public sealed record RiderSectorProfileDto(
    [property: JsonPropertyName("start")] double? Start,
    [property: JsonPropertyName("middle")] double? Middle,
    [property: JsonPropertyName("finish")] double? Finish,
    [property: JsonPropertyName("technical")] double? Technical,
    [property: JsonPropertyName("fast")] double? Fast,
    [property: JsonPropertyName("consistency")] double? Consistency,
    [property: JsonPropertyName("peak")] double? Peak,
    [property: JsonPropertyName("weak")] double? Weak,
    [property: JsonPropertyName("variance")] double? Variance);

public sealed record RiderSimilarityPairDto(
    [property: JsonPropertyName("riderAId")] string RiderAId,
    [property: JsonPropertyName("riderBId")] string RiderBId,
    [property: JsonPropertyName("similarity")] double? Similarity,
    [property: JsonPropertyName("sharedFeatures")] int SharedFeatures);

public sealed record RiderSimilarityClusterDto(
    [property: JsonPropertyName("clusterId")] int ClusterId,
    [property: JsonPropertyName("riderIds")] IReadOnlyList<string> RiderIds,
    [property: JsonPropertyName("size")] int Size,
    [property: JsonPropertyName("centroid")] RiderSectorProfileDto Centroid,
    [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags);

public sealed record RiderSimilarityMergeDto(
    [property: JsonPropertyName("nodeId")] string NodeId,
    [property: JsonPropertyName("leftId")] string LeftId,
    [property: JsonPropertyName("rightId")] string RightId,
    [property: JsonPropertyName("distance")] double Distance,
    [property: JsonPropertyName("size")] int Size);

public sealed record RiderSimilarityMetaDto(
    [property: JsonPropertyName("riderCount")] int RiderCount,
    [property: JsonPropertyName("sectorObservationCount")] int SectorObservationCount,
    [property: JsonPropertyName("sessionCount")] int SessionCount,
    [property: JsonPropertyName("featureNames")] IReadOnlyList<string> FeatureNames,
    [property: JsonPropertyName("dataSource")] string DataSource);
