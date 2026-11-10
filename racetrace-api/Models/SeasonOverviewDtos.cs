namespace RaceTrace.Api.Models;

public sealed record SeasonOverviewResponseDto(
    int Season,
    SeasonSummaryRoundDto? NextRound,
    SeasonSummaryRoundDto? LastCompletedRound,
    SeasonProgressDto Progress
);
