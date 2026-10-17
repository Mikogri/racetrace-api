using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Infrastructure.Supabase;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Services;

internal sealed class RiderTeamHistoryQueries(ISupabaseRestClient supabase)
{
    private const int ResultsPageSize = 1000;
    private const int RiderFilterBatchSize = 100;
    private readonly ISupabaseRestClient _supabase = supabase;
    private readonly object _teamHistoryLock = new();
    private readonly Dictionary<(string Ids, DateOnly? From, DateOnly? To), Task<IReadOnlyList<JsonObject>>> _teamHistoryReads = [];

    public async Task<IReadOnlyList<JsonObject>> GetRiderTeamHistoryAsync(
        IEnumerable<string> riderIds,
        DateOnly? fromDate = null,
        DateOnly? toDate = null,
        SupabaseRole role = SupabaseRole.Anon,
        CancellationToken cancellationToken = default)
    {
        var ids = riderIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        // Scoped to this HTTP request, not a cross-request cache. Import reads must
        // remain fresh because privileged callers may update history in the same scope.
        if (role != SupabaseRole.Anon)
            return await LoadRiderTeamHistoryAsync(ids, fromDate, toDate, role, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        var key = (string.Join('\u001f', ids), fromDate, toDate);
        Task<IReadOnlyList<JsonObject>> pending;
        lock (_teamHistoryLock)
        {
            if (!_teamHistoryReads.TryGetValue(key, out pending!))
            {
                pending = LoadRiderTeamHistoryAsync(ids, fromDate, toDate, role, cancellationToken);
                _teamHistoryReads[key] = pending;
            }
        }
        try
        {
            var history = await pending.WaitAsync(cancellationToken);
            return history.Select(row => row.DeepClone().AsObject()).ToArray();
        }
        catch
        {
            if (pending.IsFaulted || pending.IsCanceled)
                lock (_teamHistoryLock)
                    if (_teamHistoryReads.TryGetValue(key, out var current) && ReferenceEquals(current, pending))
                        _teamHistoryReads.Remove(key);
            throw;
        }
    }

    private async Task<IReadOnlyList<JsonObject>> LoadRiderTeamHistoryAsync(
        string[] ids, DateOnly? fromDate, DateOnly? toDate, SupabaseRole role, CancellationToken cancellationToken)
    {
        var rows = new List<JsonObject>();
        foreach (var batch in ids.Chunk(RiderFilterBatchSize))
        {
            var query = new SupabaseQuery("rider_team_history")
                .Select(
                    "id,rider_id,team_id,valid_from,valid_to,source_round_id," +
                    "teams(id,code,name),rounds(id,season_year,round_number,start_date)")
                .In("rider_id", batch)
                .Filter("valid_from", "lte", toDate)
                .Order("valid_from", descending: true)
                .Limit(ResultsPageSize);
            if (fromDate.HasValue)
            {
                query.Or($"valid_to.is.null,valid_to.gte.{fromDate:yyyy-MM-dd}");
            }

            var response = await _supabase.GetAsync(
                query,
                role: role,
                cancellationToken: cancellationToken);
            rows.AddRange(response.Data);
        }

        return rows;
    }

    public async Task DecorateMissingResultTeamsFromHistoryAsync(
        IReadOnlyList<JsonObject> results,
        CancellationToken cancellationToken = default)
    {
        var missing = results
            .Where(result =>
                result.Object("teams") is null &&
                string.IsNullOrWhiteSpace(result.String("team_id")) &&
                !string.IsNullOrWhiteSpace(result.String("rider_id")))
            .ToArray();
        if (missing.Length == 0)
        {
            return;
        }

        var dated = missing
            .Select(result => (Result: result, Date: ResultDateOnly(result)))
            .Where(item => item.Date.HasValue)
            .ToArray();
        if (dated.Length == 0)
        {
            return;
        }

        // Missing result teams often come from sources such as historical PDFs.
        // Load the complete history so an older result can use the earliest known
        // assignment when the history starts after that event.
        var history = await GetRiderTeamHistoryAsync(
            dated.Select(item => item.Result.String("rider_id")!),
            cancellationToken: cancellationToken);
        foreach (var item in dated)
        {
            var riderId = item.Result.String("rider_id")!;
            var period = FindTeamPeriodOrNearest(history, riderId, item.Date!.Value);
            if (period?.Object("teams") is not { } team)
            {
                continue;
            }

            item.Result["team_id"] = team["id"]?.DeepClone();
            item.Result["teams"] = team.DeepClone();
        }
    }

    public async Task DecorateRankingRiderTeamsAsync(
        IReadOnlyList<JsonObject> results,
        CancellationToken cancellationToken = default)
    {
        var latestDates = results
            .Where(result => !string.IsNullOrWhiteSpace(result.String("rider_id")))
            .Select(result => new
            {
                RiderId = result.String("rider_id")!,
                Date = ResultDateOnly(result)
            })
            .Where(item => item.Date.HasValue)
            .GroupBy(item => item.RiderId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Max(item => item.Date!.Value),
                StringComparer.OrdinalIgnoreCase);
        if (latestDates.Count == 0)
        {
            return;
        }

        var history = await GetRiderTeamHistoryAsync(
            latestDates.Keys,
            latestDates.Values.Min(),
            latestDates.Values.Max(),
            cancellationToken: cancellationToken);
        foreach (var result in results)
        {
            var riderId = result.String("rider_id");
            if (string.IsNullOrWhiteSpace(riderId) ||
                result.Object("riders") is not { } rider ||
                !latestDates.TryGetValue(riderId, out var effectiveDate))
            {
                continue;
            }

            if (result.Object("teams") is null &&
                string.IsNullOrWhiteSpace(result.String("team_id")) &&
                ResultDateOnly(result) is { } resultDate &&
                FindTeamPeriod(history, riderId, resultDate) is { } resultPeriod &&
                resultPeriod.Object("teams") is { } resultTeam)
            {
                result["team_id"] = resultTeam["id"]?.DeepClone();
                result["teams"] = resultTeam.DeepClone();
            }

            var team = FindTeamPeriod(history, riderId, effectiveDate)?
                .Object("teams");
            rider["teams"] = team is null
                ? new JsonArray()
                : new JsonArray(team.DeepClone());
        }
    }

    internal static JsonObject? FindTeamPeriod(
        IEnumerable<JsonObject> history,
        string riderId,
        DateOnly effectiveDate)
    {
        return history
            .Where(row => string.Equals(
                row.String("rider_id"),
                riderId,
                StringComparison.OrdinalIgnoreCase))
            .Where(row =>
                PublicDtoMapping.Date(row.String("valid_from")) is { } validFrom &&
                validFrom <= effectiveDate &&
                (PublicDtoMapping.Date(row.String("valid_to")) is not { } validTo ||
                    validTo >= effectiveDate))
            .OrderByDescending(row => row.String("valid_from"), StringComparer.Ordinal)
            .FirstOrDefault();
    }

    internal static JsonObject? FindTeamPeriodOrNearest(
        IEnumerable<JsonObject> history,
        string riderId,
        DateOnly effectiveDate)
    {
        var riderHistory = history
            .Where(row => string.Equals(
                row.String("rider_id"),
                riderId,
                StringComparison.OrdinalIgnoreCase))
            .Select(row => new
            {
                Row = row,
                ValidFrom = PublicDtoMapping.Date(row.String("valid_from")),
                ValidTo = PublicDtoMapping.Date(row.String("valid_to"))
            })
            .Where(item => item.ValidFrom.HasValue)
            .ToArray();

        var exact = FindTeamPeriod(
            riderHistory.Select(item => item.Row),
            riderId,
            effectiveDate);
        if (exact is not null)
        {
            return exact;
        }

        return riderHistory
            .OrderBy(item => DistanceFromPeriod(effectiveDate, item.ValidFrom!.Value, item.ValidTo))
            .ThenBy(item => item.ValidFrom > effectiveDate ? 1 : 0)
            .ThenByDescending(item => item.ValidFrom)
            .Select(item => item.Row)
            .FirstOrDefault();
    }

    private static int DistanceFromPeriod(
        DateOnly effectiveDate,
        DateOnly validFrom,
        DateOnly? validTo)
    {
        if (effectiveDate < validFrom)
        {
            return validFrom.DayNumber - effectiveDate.DayNumber;
        }

        return validTo is { } end && effectiveDate > end
            ? effectiveDate.DayNumber - end.DayNumber
            : 0;
    }

    private static DateOnly? ResultDateOnly(JsonObject result) =>
        PublicDtoMapping.Date(DomainQueryService.ResultDate(result));

}
