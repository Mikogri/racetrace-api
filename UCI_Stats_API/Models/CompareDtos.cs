using System.Text.Json.Serialization;

namespace UCI_Stats_API.Models;

public sealed record CompareResponseDto(
    [property: JsonPropertyName("riders")] IReadOnlyList<CompareRiderDto> Riders,
    [property: JsonPropertyName("commonSessions")] IReadOnlyList<CompareSessionDto> CommonSessions,
    [property: JsonPropertyName("summary")] CompareSummaryDto Summary);

public sealed record CompareSummaryDto(
    [property: JsonPropertyName("ridersCount")] int RidersCount,
    [property: JsonPropertyName("commonSessionsCount")] int CommonSessionsCount);

public sealed record CompareRiderDto(
    [property: JsonPropertyName("riderId")] long RiderId,
    [property: JsonPropertyName("uciId")] string? UciId,
    [property: JsonPropertyName("firstName")] string? FirstName,
    [property: JsonPropertyName("lastName")] string? LastName,
    [property: JsonPropertyName("fullName")] string FullName,
    [property: JsonPropertyName("countryCode")] string? CountryCode,
    [property: JsonPropertyName("profileImageUrl")] string? ProfileImageUrl,
    [property: JsonPropertyName("teamId")] long? TeamId,
    [property: JsonPropertyName("teamName")] string? TeamName,
    [property: JsonPropertyName("teamCode")] string? TeamCode,
    [property: JsonPropertyName("winsCount")] int WinsCount,
    [property: JsonPropertyName("podiumsCount")] int PodiumsCount,
    [property: JsonPropertyName("top5Count")] int Top5Count,
    [property: JsonPropertyName("top10Count")] int Top10Count,
    [property: JsonPropertyName("rating")] decimal? Rating,
    [property: JsonPropertyName("pace")] decimal? Pace,
    [property: JsonPropertyName("consistency")] decimal? Consistency,
    [property: JsonPropertyName("confidence")] decimal Confidence,
    [property: JsonPropertyName("wonSplitsCount")] int WonSplitsCount,
    [property: JsonPropertyName("wonSectorsCount")] int WonSectorsCount,
    [property: JsonPropertyName("averageFinalPosition")] decimal? AverageFinalPosition,
    [property: JsonPropertyName("averagePreFinalPosition")] decimal? AveragePreFinalPosition,
    [property: JsonPropertyName("averageFinishGapToBestSelected")] decimal? AverageFinishGapToBestSelected,
    [property: JsonPropertyName("finishGapSamplesCount")] int FinishGapSamplesCount,
    [property: JsonPropertyName("averageSectorGapsToBestSelected")] IReadOnlyList<CompareAverageSectorGapDto> AverageSectorGapsToBestSelected);

public sealed record CompareAverageSectorGapDto(
    [property: JsonPropertyName("trackId")] long? TrackId,
    [property: JsonPropertyName("trackName")] string? TrackName,
    [property: JsonPropertyName("sectorIndex")] int SectorIndex,
    [property: JsonPropertyName("isFinishSector")] bool IsFinishSector,
    [property: JsonPropertyName("averageGap")] decimal AverageGap,
    [property: JsonPropertyName("samplesCount")] int SamplesCount);

public sealed record CompareSessionDto(
    [property: JsonPropertyName("sessionId")] long SessionId,
    [property: JsonPropertyName("roundId")] long RoundId,
    [property: JsonPropertyName("season")] int? Season,
    [property: JsonPropertyName("roundNumber")] int? RoundNumber,
    [property: JsonPropertyName("sessionName")] string? SessionName,
    [property: JsonPropertyName("sessionType")] string? SessionType,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("sessionDate")] DateOnly? SessionDate,
    [property: JsonPropertyName("trackId")] long? TrackId,
    [property: JsonPropertyName("trackName")] string? TrackName,
    [property: JsonPropertyName("venueName")] string? VenueName,
    [property: JsonPropertyName("countryCode")] string? CountryCode,
    [property: JsonPropertyName("condition")] ConditionDto? Condition,
    [property: JsonPropertyName("competitionClassId")] long? CompetitionClassId,
    [property: JsonPropertyName("competitionClassCode")] string? CompetitionClassCode,
    [property: JsonPropertyName("competitionClassName")] string? CompetitionClassName,
    [property: JsonPropertyName("results")] IReadOnlyList<CompareSessionResultDto> Results);

public sealed record CompareSessionResultDto(
    [property: JsonPropertyName("resultId")] long ResultId,
    [property: JsonPropertyName("riderId")] long RiderId,
    [property: JsonPropertyName("position")] int? Position,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("isClassified")] bool IsClassified,
    [property: JsonPropertyName("teamId")] long? TeamId,
    [property: JsonPropertyName("teamName")] string? TeamName,
    [property: JsonPropertyName("finalTime")] decimal? FinalTime,
    [property: JsonPropertyName("gapToBestSelected")] decimal? GapToBestSelected,
    [property: JsonPropertyName("sectors")] IReadOnlyList<CompareSectorResultDto> Sectors);

public sealed record CompareSectorResultDto(
    [property: JsonPropertyName("sectorIndex")] int SectorIndex,
    [property: JsonPropertyName("isFinishSector")] bool IsFinishSector,
    [property: JsonPropertyName("time")] decimal? Time,
    [property: JsonPropertyName("gapToBestSelected")] decimal? GapToBestSelected,
    [property: JsonPropertyName("positionAmongSelected")] int? PositionAmongSelected);
