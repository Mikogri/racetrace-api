using System.Text.Json.Nodes;

namespace RaceTrace.Api.Models;

public sealed record SessionSummaryDto(
    long SessionId,
    long RoundId,
    string? SessionType,
    string? Category,
    ConditionDto? Condition,
    int CompletedRidersCount,
    int StartedRidersCount,
    int SplitCount,
    int SectorCount,
    string TimeView
);

public sealed record ResultTimeDto(
    int Index,
    string Name,
    decimal? Time,
    decimal? GapToLeader,
    int? Position
)
{
    public decimal? GapToBest => GapToLeader;
}

public sealed record SessionResultDto(
    int? Position,
    long? ResultId,
    long? RiderId,
    string? UciId,
    string? FirstName,
    string? LastName,
    string FullName,
    string? CountryCode,
    long? TeamId,
    string? TeamName,
    string? Status,
    decimal? TopSpeed,
    decimal? AverageSpeed,
    decimal? FinalTime,
    decimal? FinalGapToLeader,
    decimal AwardedPoints,
    IReadOnlyList<ResultTimeDto> Times
)
{
    public bool IsPerfectRun { get; init; }

    public string? SessionType { get; init; }

    public decimal? FinalGapToBest => FinalGapToLeader;
}

public sealed record SessionResultsResponseDto(
    SessionSummaryDto Session,
    IReadOnlyList<SessionResultDto> Items,
    int Page,
    int PageSize,
    int TotalItems,
    int TotalPages,
    bool HasPreviousPage,
    bool HasNextPage
);

public sealed record CommonSessionDto(
    long SessionId,
    long RoundId,
    string? SessionType,
    string? Category,
    DateOnly? SessionDate,
    IReadOnlyList<long> RiderIds
);
