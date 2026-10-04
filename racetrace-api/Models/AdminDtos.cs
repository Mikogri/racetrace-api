using System.Text.Json.Nodes;

namespace RaceTrace.Api.Models;

public sealed record ChronoRaceImportRequest(
    ImportRoundPayload Round,
    ImportTrackPayload Track,
    ImportCompetitionClassPayload? CompetitionClass,
    ImportConditionPayload? Condition,
    string CategoryCode,
    ImportSessionPayload Session,
    JsonObject ChronoraceJson);

public sealed record ImportRoundPayload(
    string ContextName,
    int SeasonYear,
    int? RoundNumber,
    DateTimeOffset? StartDate,
    DateTimeOffset? EndDate,
    long? CompetitionClassId = null,
    string? CompetitionClassCode = null,
    bool IsSeasonFinalRound = false,
    string? Status = null);

public sealed record ImportTrackPayload(
    string Name,
    string Venue,
    string? CountryCode,
    int? LengthM,
    decimal? AvgSlope);

public sealed record ImportCompetitionClassPayload(
    string Code,
    string Name,
    int? Priority);

public sealed record ImportConditionPayload(
    int? Id,
    string? Name,
    bool? IsDry);

public sealed record ImportSessionPayload(
    string Name,
    string SessionType,
    DateOnly? SessionDate,
    string? Status,
    string? ChronoraceUrl = null);

public sealed record ChronoRaceImportResponse(
    string RoundId,
    string SessionId,
    int RidersCreated,
    int RidersUpdated,
    int ResultsUpserted,
    int SplitsUpserted,
    bool PointsRecalculated,
    IReadOnlyList<string> Warnings);

public sealed record ChronoRaceSessionReimportRequest(
    string? ChronoraceUrl,
    JsonObject? ChronoraceJson,
    JsonObject? ResultJson = null);

public sealed record ChronoRaceSessionReimportResponse(
    string SessionId,
    string? ChronoraceUrl,
    string? ResolvedChronoraceJsonUrl,
    ChronoRaceImportResponse Import,
    IReadOnlyList<string> Warnings);

public sealed record UciMtbWorldSeriesSupplementResponse(
    string SessionId,
    int SourceResults,
    int MatchedResults,
    int ResultsUpdated,
    int SplitsUpserted,
    int SkippedResults,
    bool PointsRecalculated,
    IReadOnlyList<string> Warnings);

public sealed record RiderProfileImageImportResponse(
    int FilesProcessed,
    int ImagesFound,
    int RidersUpdated,
    int Unchanged,
    int UnmatchedRiders,
    int InvalidUrls,
    IReadOnlyList<string> Warnings);

public sealed record ChronoRaceRoundFormImportRequest(
    ImportRoundPayload Round,
    ImportTrackPayload Track,
    ImportCompetitionClassPayload? CompetitionClass,
    ImportConditionPayload? Condition,
    IReadOnlyList<ChronoRaceRoundFormSessionPayload> Sessions,
    ImportRoundImagePayload? Image = null);

public sealed record ImportRoundImagePayload(
    string ImageBase64,
    string? FileName,
    string? AltText,
    int? WidthPx,
    int? HeightPx);

public sealed record PlannedRoundCreateRequest(
    int SeasonYear,
    int? RoundNumber,
    DateTimeOffset StartDate,
    DateTimeOffset EndDate,
    string Status,
    long CompetitionClassId,
    string CompetitionClassCode,
    string CompetitionClassName,
    int? CompetitionClassPriority,
    long? TrackId,
    ImportTrackPayload? Track,
    int ConditionId,
    IReadOnlyList<string> CategoryCodes,
    bool IsSeasonFinalRound = false,
    ImportRoundImagePayload? Image = null,
    string? ContextName = null);

public sealed record PlannedRoundCreateResponse(
    string RoundId,
    string Status,
    string TrackId,
    IReadOnlyList<string> CategoryCodes,
    bool ImageUpserted);

public sealed record ChronoRaceRoundFormSessionPayload(
    string Name,
    string SessionType,
    DateOnly? SessionDate,
    string? Status,
    ImportConditionPayload? Condition,
    IReadOnlyList<ChronoRaceRoundFormCategoryPayload> Categories);

public sealed record ChronoRaceRoundFormCategoryPayload(
    string CategoryCode,
    string? ChronoraceUrl,
    JsonObject? ChronoraceJson,
    JsonObject? ResultJson = null,
    string? ResultUrl = null);

public sealed record PdfRoundFormImportRequest(
    ImportRoundPayload Round,
    ImportTrackPayload Track,
    ImportCompetitionClassPayload? CompetitionClass,
    ImportConditionPayload? Condition,
    IReadOnlyList<PdfRoundFormSessionPayload> Sessions,
    ImportRoundImagePayload? Image = null);

public sealed record PdfRoundFormSessionPayload(
    string Name,
    string SessionType,
    DateOnly? SessionDate,
    string? Status,
    ImportConditionPayload? Condition,
    IReadOnlyList<PdfRoundFormCategoryPayload> Categories);

public sealed record PdfRoundFormCategoryPayload(
    string CategoryCode,
    string PdfFileName);

public sealed record ChronoRaceRoundFormImportResponse(
    string? RoundId,
    int SessionsImported,
    int CategoriesImported,
    int RidersCreated,
    int RidersUpdated,
    int ResultsUpserted,
    int SplitsUpserted,
    int PointsRecalculated,
    IReadOnlyList<ChronoRaceRoundFormImportItemResponse> Items,
    IReadOnlyList<string> Warnings,
    bool ImageUpserted);

public sealed record ChronoRaceRoundFormImportItemResponse(
    string SessionName,
    string SessionType,
    string CategoryCode,
    string? ChronoraceUrl,
    string? ResolvedChronoraceJsonUrl,
    string? RoundId,
    string? SessionId,
    int RidersCreated,
    int RidersUpdated,
    int ResultsUpserted,
    int SplitsUpserted,
    bool PointsRecalculated,
    IReadOnlyList<string> Warnings);

public sealed record PointsRecalculateRequest(
    string? SessionId,
    int? SeasonYear,
    string? CategoryCode,
    string? Scope);

public sealed record RankingPreaggregationRefreshRequest(string? Mode);
