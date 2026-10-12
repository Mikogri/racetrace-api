using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace RaceTrace.Api.Models;

public sealed record SessionSummaryDto(
    [property: JsonPropertyName("sessionId")] long SessionId,
    [property: JsonPropertyName("roundId")] long RoundId,
    [property: JsonPropertyName("sessionType")] string? SessionType,
    string? Category,
    ConditionDto? Condition,
    [property: JsonPropertyName("completedRidersCount")] int CompletedRidersCount,
    [property: JsonPropertyName("startedRidersCount")] int StartedRidersCount,
    [property: JsonPropertyName("splitCount")] int SplitCount,
    [property: JsonPropertyName("sectorCount")] int SectorCount,
    [property: JsonPropertyName("timeView")] string TimeView);

public sealed record ResultTimeDto(
    int Index,
    string Name,
    decimal? Time,
    [property: JsonPropertyName("gapToLeader")] decimal? GapToLeader,
    int? Position)
{
    [JsonPropertyName("gapToBest")]
    public decimal? GapToBest => GapToLeader;
}

public sealed record SessionResultDto(
    int? Position,
    [property: JsonPropertyName("resultId")] long? ResultId,
    [property: JsonPropertyName("riderId")] long? RiderId,
    [property: JsonPropertyName("uciId")] string? UciId,
    [property: JsonPropertyName("firstName")] string? FirstName,
    [property: JsonPropertyName("lastName")] string? LastName,
    [property: JsonPropertyName("fullName")] string FullName,
    [property: JsonPropertyName("countryCode")] string? CountryCode,
    [property: JsonPropertyName("teamId")] long? TeamId,
    [property: JsonPropertyName("teamName")] string? TeamName,
    string? Status,
    [property: JsonPropertyName("topSpeed")] decimal? TopSpeed,
    [property: JsonPropertyName("averageSpeed")] decimal? AverageSpeed,
    [property: JsonPropertyName("finalTime")] decimal? FinalTime,
    [property: JsonPropertyName("finalGapToLeader")] decimal? FinalGapToLeader,
    [property: JsonPropertyName("awardedPoints")] decimal AwardedPoints,
    IReadOnlyList<ResultTimeDto> Times)
{
    [JsonPropertyName("isPerfectRun")]
    public bool IsPerfectRun { get; init; }

    [JsonPropertyName("sessionType")]
    public string? SessionType { get; init; }

    [JsonPropertyName("finalGapToBest")]
    public decimal? FinalGapToBest => FinalGapToLeader;
}

public sealed record SessionResultsResponseDto(
    SessionSummaryDto Session,
    IReadOnlyList<SessionResultDto> Items,
    int Page,
    [property: JsonPropertyName("pageSize")] int PageSize,
    [property: JsonPropertyName("totalItems")] int TotalItems,
    [property: JsonPropertyName("totalPages")] int TotalPages,
    [property: JsonPropertyName("hasPreviousPage")] bool HasPreviousPage,
    [property: JsonPropertyName("hasNextPage")] bool HasNextPage);

public sealed record CommonSessionDto(
    [property: JsonPropertyName("sessionId")] long SessionId,
    [property: JsonPropertyName("roundId")] long RoundId,
    [property: JsonPropertyName("sessionType")] string? SessionType,
    string? Category,
    [property: JsonPropertyName("sessionDate")] DateOnly? SessionDate,
    [property: JsonPropertyName("riderIds")] IReadOnlyList<long> RiderIds);
