namespace RaceTrace.Api.Models;

public sealed record HomeRankingResponseDto(
    RankingResponseDto TopRated,
    RankingResponseDto Standings);
