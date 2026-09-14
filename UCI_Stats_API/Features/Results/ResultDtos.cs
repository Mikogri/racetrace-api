using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace UCI_Stats_API.Models;

public sealed record SessionSummaryDto(
    [property: JsonPropertyName("sessionId")] long SessionId,
    [property: JsonPropertyName("roundId")] long RoundId,
    [property: JsonPropertyName("sessionType")] string? SessionType,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("condition")] ConditionDto? Condition,
    [property: JsonPropertyName("completedRidersCount")] int CompletedRidersCount,
    [property: JsonPropertyName("startedRidersCount")] int StartedRidersCount,
    [property: JsonPropertyName("splitCount")] int SplitCount,
    [property: JsonPropertyName("sectorCount")] int SectorCount,
    [property: JsonPropertyName("timeView")] string TimeView);

public sealed record ResultTimeDto(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("time")] decimal? Time,
    [property: JsonPropertyName("gapToLeader")] decimal? GapToLeader,
    [property: JsonPropertyName("position")] int? Position)
{
    [JsonPropertyName("gapToBest")]
    public decimal? GapToBest => GapToLeader;
}

public sealed record SessionResultDto(
    [property: JsonPropertyName("position")] int? Position,
    [property: JsonPropertyName("resultId")] long? ResultId,
    [property: JsonPropertyName("riderId")] long? RiderId,
    [property: JsonPropertyName("uciId")] string? UciId,
    [property: JsonPropertyName("firstName")] string? FirstName,
    [property: JsonPropertyName("lastName")] string? LastName,
    [property: JsonPropertyName("fullName")] string FullName,
    [property: JsonPropertyName("countryCode")] string? CountryCode,
    [property: JsonPropertyName("teamId")] long? TeamId,
    [property: JsonPropertyName("teamName")] string? TeamName,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("topSpeed")] decimal? TopSpeed,
    [property: JsonPropertyName("averageSpeed")] decimal? AverageSpeed,
    [property: JsonPropertyName("finalTime")] decimal? FinalTime,
    [property: JsonPropertyName("finalGapToLeader")] decimal? FinalGapToLeader,
    [property: JsonPropertyName("awardedPoints")] decimal AwardedPoints,
    [property: JsonPropertyName("times")] IReadOnlyList<ResultTimeDto> Times)
{
    [JsonPropertyName("isPerfectRun")]
    public bool IsPerfectRun { get; init; }

    [JsonPropertyName("finalGapToBest")]
    public decimal? FinalGapToBest => FinalGapToLeader;
}

public sealed record SessionResultsResponseDto(
    [property: JsonPropertyName("session")] SessionSummaryDto Session,
    [property: JsonPropertyName("items")] IReadOnlyList<SessionResultDto> Items,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("pageSize")] int PageSize,
    [property: JsonPropertyName("totalItems")] int TotalItems,
    [property: JsonPropertyName("totalPages")] int TotalPages,
    [property: JsonPropertyName("hasPreviousPage")] bool HasPreviousPage,
    [property: JsonPropertyName("hasNextPage")] bool HasNextPage);

public sealed record CommonSessionDto(
    [property: JsonPropertyName("sessionId")] long SessionId,
    [property: JsonPropertyName("roundId")] long RoundId,
    [property: JsonPropertyName("sessionType")] string? SessionType,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("sessionDate")] DateOnly? SessionDate,
    [property: JsonPropertyName("riderIds")] IReadOnlyList<long> RiderIds);
