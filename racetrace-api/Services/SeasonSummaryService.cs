using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Services;

public sealed class SeasonSummaryService
{
    private const int SummaryListSize = 5;

    private readonly ISupabaseRestClient _supabase;
    private readonly QueryValidationService _validator;
    private readonly DomainQueryService _domainQueries;
    private readonly RankingService _ranking;
    private readonly TimeProvider _timeProvider;

    public SeasonSummaryService(
        ISupabaseRestClient supabase,
        QueryValidationService validator,
        DomainQueryService domainQueries,
        RankingService ranking,
        TimeProvider timeProvider)
    {
        _supabase = supabase;
        _validator = validator;
        _domainQueries = domainQueries;
        _ranking = ranking;
        _timeProvider = timeProvider;
    }

    public async Task<SeasonOverviewResponseDto> GetOverviewAsync(
        int season,
        CancellationToken cancellationToken)
    {
        if (season is < 1990 or > 2100)
        {
            throw _validator.BadQuery("season must be between 1990 and 2100.");
        }

        var rounds = await LoadRoundsAsync(season, cancellationToken);
        var roundIds = rounds
            .Select(round => round.String("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToArray();
        var sessions = await LoadSessionsAsync(roundIds, cancellationToken);
        var finalSessionIds = sessions
            .Where(DomainQueryService.IsFinalSession)
            .Select(session => session.String("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var finalWinnersTask = LoadFinalWinnerIdsAsync(finalSessionIds, cancellationToken);
        var now = _timeProvider.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var nextRound = rounds
            .Where(round =>
                string.Equals(RoundsService.RoundStatus(round, now), "upcoming", StringComparison.OrdinalIgnoreCase) &&
                PublicDtoMapping.Date(round.String("start_date")) is { } startDate &&
                startDate >= today)
            .OrderBy(round => PublicDtoMapping.Date(round.String("start_date")))
            .ThenBy(round => PublicDtoMapping.Long(round.String("id")))
            .FirstOrDefault();
        var completedRounds = rounds
            .Where(round => string.Equals(
                RoundsService.RoundStatus(round, now),
                "completed",
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(round => PublicDtoMapping.Date(round.String("start_date")))
            .ThenByDescending(round => PublicDtoMapping.Long(round.String("id")))
            .ToArray();
        var categoriesCount = sessions
            .Select(session => session.String("category_id"))
            .Concat(rounds.SelectMany(RoundCategoryIds))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var finalWinners = await finalWinnersTask;

        return new SeasonOverviewResponseDto(
            season,
            nextRound is null ? null : ToRoundDto(nextRound, now),
            completedRounds.FirstOrDefault() is { } lastRound
                ? ToRoundDto(lastRound, now)
                : null,
            new SeasonProgressDto(
                rounds.Count,
                completedRounds.Length,
                finalWinners.Count,
                categoriesCount));
    }

    public async Task<SeasonSummaryResponseDto> GetAsync(
        IQueryCollection queryString,
        CancellationToken cancellationToken)
    {
        var season = ReadSeason(queryString);
        var (publicCategory, categoryCode) = await ReadCategoryAsync(queryString, cancellationToken);
        var rounds = await LoadRoundsAsync(season, cancellationToken);
        var roundIds = rounds
            .Select(round => round.String("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToArray();
        var sessions = await LoadSessionsAsync(roundIds, cancellationToken);
        DecorateSessions(sessions, rounds);
        AttachSessionsToRounds(rounds, sessions);

        var categoryId = FindCategoryId(rounds, categoryCode) ??
            await _domainQueries.FindCategoryIdByCodeAsync(
                categoryCode,
                cancellationToken: cancellationToken);
        var categorySessions = string.IsNullOrWhiteSpace(categoryId)
            ? []
            : sessions
                .Where(session => string.Equals(
                    session.String("category_id"),
                    categoryId,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
        var context = SeasonContext(season, categoryCode);
        var finalSessionIds = sessions
            .Where(DomainQueryService.IsFinalSession)
            .Select(session => session.String("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var rankingTask = _ranking.BuildSnapshotAsync(
            context,
            cancellationToken,
            categorySessions);
        var finalWinnersTask = LoadFinalWinnerIdsAsync(
            finalSessionIds,
            cancellationToken);
        await Task.WhenAll(rankingTask, finalWinnersTask);
        var snapshot = await rankingTask;
        var finalWinners = await finalWinnersTask;

        var now = _timeProvider.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var nextRound = rounds
            .Where(round =>
                string.Equals(RoundsService.RoundStatus(round, now), "upcoming", StringComparison.OrdinalIgnoreCase) &&
                PublicDtoMapping.Date(round.String("start_date")) is { } startDate &&
                startDate >= today)
            .OrderBy(round => PublicDtoMapping.Date(round.String("start_date")))
            .ThenBy(round => PublicDtoMapping.Long(round.String("id")))
            .FirstOrDefault();
        var completedRounds = rounds
            .Where(round => string.Equals(
                RoundsService.RoundStatus(round, now),
                "completed",
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(round => PublicDtoMapping.Date(round.String("start_date")))
            .ThenByDescending(round => PublicDtoMapping.Long(round.String("id")))
            .ToArray();
        var finalResultRoundIds = snapshot.Results
            .Where(DomainQueryService.IsFinalResult)
            .Where(IsClassified)
            .Select(ResultRoundId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lastCompletedRound = completedRounds
            .FirstOrDefault(round => finalResultRoundIds.Contains(round.String("id") ?? string.Empty)) ??
            completedRounds.FirstOrDefault();
        var categoryIds = sessions
            .Select(session => session.String("category_id"))
            .Concat(rounds.SelectMany(RoundCategoryIds))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        return new SeasonSummaryResponseDto(
            season,
            publicCategory,
            nextRound is null ? null : ToRoundDto(nextRound, now),
            lastCompletedRound is null ? null : ToRoundDto(lastCompletedRound, now),
            BuildLastRoundResults(snapshot.Results, lastCompletedRound),
            new SeasonProgressDto(
                rounds.Count,
                completedRounds.Length,
                finalWinners.Count,
                categoryIds),
            BuildTopRated(snapshot.Rows),
            BuildStandings(snapshot.Rows));
    }

    internal int ReadSeason(IQueryCollection queryString)
    {
        var season = _validator.IntAny(
            queryString,
            "season",
            "seasonYear",
            "season_year") ?? _timeProvider.GetUtcNow().Year;
        if (season is < 1990 or > 2100)
        {
            throw _validator.BadQuery("season must be between 1990 and 2100.");
        }

        return season;
    }

    internal async Task<(string PublicCategory, string DatabaseCategory)> ReadCategoryAsync(
        IQueryCollection queryString,
        CancellationToken cancellationToken)
    {
        var value = _validator.StringAny(
            queryString,
            "category",
            "categoryCode",
            "category_code") ?? "elite_men";
        try
        {
            var databaseCategory = await _domainQueries.ResolveCategoryCodeAsync(value, cancellationToken);
            return (
                PublicApiCodes.ToPublicCategory(databaseCategory) ?? value,
                databaseCategory);
        }
        catch (ArgumentException)
        {
            throw _validator.BadQuery(
                $"Unsupported category '{value}'. Allowed values: {string.Join(", ", PublicApiCodes.PublicCategories)}.");
        }
    }

    internal static SeasonSummaryRoundDto ToRoundDto(
        JsonObject round,
        DateTimeOffset? now = null)
    {
        var track = round.Object("tracks");
        var competitionClass = round.Object("competition_class") ??
            round.Object("competition_levels");
        return new SeasonSummaryRoundDto(
            PublicDtoMapping.Long(round.String("id")),
            round.Int("season_year") ?? 0,
            round.Int("round_number"),
            PublicDtoMapping.Date(round.String("start_date")),
            PublicDtoMapping.Date(round.String("end_date")),
            RoundsService.RoundStatus(round, now),
            PublicDtoMapping.NullableLong(round.String("track_id") ?? track?.String("id")),
            track?.String("name"),
            track?.String("venue"),
            track?.String("country_code"),
            track?.Decimal("length_m"),
            track?.Decimal("avg_slope"),
            PublicDtoMapping.NullableLong(
                round.String("competition_class_id") ??
                round.String("competition_level_id") ??
                competitionClass?.String("id")),
            competitionClass?.String("code"),
            competitionClass?.String("name"),
            round.Int("competition_class_priority") ?? competitionClass?.Int("priority"));
    }

    private async Task<IReadOnlyList<JsonObject>> LoadRoundsAsync(
        int season,
        CancellationToken cancellationToken)
    {
        var response = await _supabase.GetAsync(
            new SupabaseQuery("rounds")
                .Select(
                    "id,context_name,season_year,round_number,track_id,start_date,end_date,competition_level_id," +
                    "tracks(id,name,venue,country_code,length_m,avg_slope)," +
                    "competition_levels(id,code,name,priority)," +
                    "round_categories(category_id,categories(id,code,name))")
                .Filter("season_year", "eq", season)
                .Add("order", "start_date.asc.nullslast,id.asc"),
            cancellationToken: cancellationToken);
        foreach (var round in response.Data)
        {
            DomainQueryService.NormalizeRoundEmbed(round);
        }

        return response.Data;
    }

    private async Task<IReadOnlyList<JsonObject>> LoadSessionsAsync(
        string[] roundIds,
        CancellationToken cancellationToken)
    {
        if (roundIds.Length == 0)
        {
            return [];
        }

        var response = await _supabase.GetAsync(
            new SupabaseQuery("sessions")
                .Select("id,round_id,category_id,name,session_type,session_date,status")
                .In("round_id", roundIds),
            cancellationToken: cancellationToken);
        return response.Data;
    }

    private async Task<IReadOnlySet<string>> LoadFinalWinnerIdsAsync(
        string[] finalSessionIds,
        CancellationToken cancellationToken)
    {
        if (finalSessionIds.Length == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var response = await _supabase.GetAsync(
            new SupabaseQuery("results")
                .Select("rider_id,position,is_classified,status")
                .In("session_id", finalSessionIds)
                .Filter("position", "eq", 1),
            cancellationToken: cancellationToken);
        return response.Data
            .Where(IsClassified)
            .Select(result => result.String("rider_id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static SeasonSummaryResultDto[] BuildLastRoundResults(
        IReadOnlyList<JsonObject> results,
        JsonObject? round)
    {
        var roundId = round?.String("id");
        if (string.IsNullOrWhiteSpace(roundId))
        {
            return [];
        }

        var finalResults = results
            .Where(DomainQueryService.IsFinalResult)
            .Where(result => string.Equals(
                ResultRoundId(result),
                roundId,
                StringComparison.OrdinalIgnoreCase))
            .Where(result => result.Int("position") is > 0)
            .Where(IsClassified)
            .OrderBy(result => result.Int("position"))
            .ThenBy(result => result.Int("race_time_ms") ?? int.MaxValue)
            .Take(SummaryListSize)
            .ToArray();
        var leaderTime = results
            .Where(DomainQueryService.IsFinalResult)
            .Where(result => string.Equals(
                ResultRoundId(result),
                roundId,
                StringComparison.OrdinalIgnoreCase))
            .Where(IsClassified)
            .Select(result => result.Int("race_time_ms"))
            .Where(time => time is > 0)
            .Select(time => time!.Value)
            .DefaultIfEmpty()
            .Min();

        return finalResults.Select(result =>
        {
            var rider = result.Object("riders") ?? new JsonObject();
            var team = result.Object("teams");
            var raceTime = result.Int("race_time_ms");
            var gap = raceTime is > 0 && leaderTime > 0
                ? PublicDtoMapping.Seconds(raceTime.Value - leaderTime)
                : null;
            return new SeasonSummaryResultDto(
                result.Int("position") ?? 0,
                PublicDtoMapping.Long(result.String("id")),
                PublicDtoMapping.Long(result.String("rider_id") ?? rider.String("id")),
                rider.DisplayName(),
                rider.String("nation"),
                PublicDtoMapping.NullableLong(team?.String("id")),
                team?.String("name"),
                PublicDtoMapping.Seconds(raceTime),
                gap);
        }).ToArray();
    }

    private static SeasonTopRatedRiderDto[] BuildTopRated(
        IReadOnlyList<JsonObject> rows)
    {
        return rows
            .Where(row => row.Decimal("rating") is not null)
            .OrderByDescending(row => row.Decimal("rating"))
            .ThenByDescending(row => row.Decimal("pace"))
            .ThenBy(row => row.String("rider_name"), StringComparer.OrdinalIgnoreCase)
            .Take(SummaryListSize)
            .Select((row, index) =>
            {
                var rider = row.Object("rider") ?? new JsonObject();
                var team = LatestTeam(rider);
                return new SeasonTopRatedRiderDto(
                    index + 1,
                    PublicDtoMapping.Long(row.String("rider_id")),
                    rider.DisplayName(),
                    rider.String("nation"),
                    PublicDtoMapping.NullableLong(team?.String("id")),
                    team?.String("name") ?? row.String("team_name"),
                    row.Decimal("rating")!.Value,
                    row.Decimal("pace"),
                    row.Decimal("consistency"),
                    row.Decimal("confidence") ?? 0m);
            })
            .ToArray();
    }

    private static SeasonStandingRiderDto[] BuildStandings(
        IReadOnlyList<JsonObject> rows)
    {
        return rows
            .OrderByDescending(row => row.Decimal("points") ?? 0m)
            .ThenByDescending(row => row.Decimal("wins") ?? 0m)
            .ThenBy(row => row.String("rider_name"), StringComparer.OrdinalIgnoreCase)
            .Take(SummaryListSize)
            .Select((row, index) =>
            {
                var rider = row.Object("rider") ?? new JsonObject();
                var team = LatestTeam(rider);
                return new SeasonStandingRiderDto(
                    index + 1,
                    PublicDtoMapping.Long(row.String("rider_id")),
                    rider.DisplayName(),
                    rider.String("nation"),
                    PublicDtoMapping.NullableLong(team?.String("id")),
                    team?.String("name") ?? row.String("team_name"),
                    row.Decimal("points") ?? 0m);
            })
            .ToArray();
    }

    private static RiderMetricsContext SeasonContext(int season, string categoryCode) =>
        new(
            season,
            categoryCode,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null)
        {
            SeasonYears = [season],
            CategoryCodes = [categoryCode]
        };

    private static void DecorateSessions(
        IReadOnlyList<JsonObject> sessions,
        IReadOnlyList<JsonObject> rounds)
    {
        var roundsById = rounds
            .Where(round => !string.IsNullOrWhiteSpace(round.String("id")))
            .ToDictionary(
                round => round.String("id")!,
                round => round,
                StringComparer.OrdinalIgnoreCase);
        foreach (var session in sessions)
        {
            if (roundsById.TryGetValue(session.String("round_id") ?? string.Empty, out var round))
            {
                session["rounds"] = round.DeepClone();
                var category = round.Array("round_categories")?
                    .OfType<JsonObject>()
                    .FirstOrDefault(roundCategory => string.Equals(
                        roundCategory.String("category_id"),
                        session.String("category_id"),
                        StringComparison.OrdinalIgnoreCase))?
                    .Object("categories");
                if (category is not null)
                {
                    session["categories"] = category.DeepClone();
                }
            }
        }
    }

    private static void AttachSessionsToRounds(
        IReadOnlyList<JsonObject> rounds,
        IReadOnlyList<JsonObject> sessions)
    {
        foreach (var round in rounds)
        {
            var roundId = round.String("id");
            round["sessions"] = new JsonArray(sessions
                .Where(session => string.Equals(
                    session.String("round_id"),
                    roundId,
                    StringComparison.OrdinalIgnoreCase))
                .Select(session => (JsonNode)new JsonObject
                {
                    ["id"] = session.String("id"),
                    ["status"] = session.String("status")
                })
                .ToArray());
        }
    }

    private static string? FindCategoryId(
        IReadOnlyList<JsonObject> rounds,
        string categoryCode)
    {
        return rounds
            .SelectMany(round => round.Array("round_categories")?.OfType<JsonObject>() ?? [])
            .FirstOrDefault(roundCategory => string.Equals(
                roundCategory.Object("categories")?.String("code"),
                categoryCode,
                StringComparison.OrdinalIgnoreCase))?
            .String("category_id");
    }

    private static IEnumerable<string?> RoundCategoryIds(JsonObject round) =>
        round.Array("round_categories")?
            .OfType<JsonObject>()
            .Select(roundCategory => roundCategory.String("category_id")) ?? [];

    private static JsonObject? LatestTeam(JsonObject rider) =>
        rider.Array("teams")?.OfType<JsonObject>().FirstOrDefault();

    private static string? ResultRoundId(JsonObject result)
    {
        var session = result.Object("sessions");
        return session?.String("round_id") ??
            session?.Object("rounds")?.String("id") ??
            session?.Object("round_categories")?.String("round_id") ??
            session?.Object("round_categories")?.Object("rounds")?.String("id");
    }

    private static bool IsClassified(JsonObject result)
    {
        var status = result.String("status");
        return (result.Bool("is_classified") == true || result.Int("position") is > 0) &&
            !string.Equals(status, "DNF", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "DNS", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "DSQ", StringComparison.OrdinalIgnoreCase);
    }
}
