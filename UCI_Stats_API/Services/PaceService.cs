using System.Text.Json.Nodes;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Models;

namespace UCI_Stats_API.Services;

public sealed class PaceService
{
    private const int PageSize = 1000;
    private const int SessionIdBatchSize = 100;
    private const int ResultIdBatchSize = 150;
    private const int MaximumConcurrentSplitQueries = 4;

    private readonly ISupabaseRestClient _supabase;
    private readonly DownhillPaceCalculator _paceCalculator;
    private readonly DownhillConsistencyCalculator _consistencyCalculator;
    private readonly DownhillResultScoreCalculator _resultScoreCalculator;
    private readonly DownhillRatingCalculator _ratingCalculator = new();

    public PaceService(ISupabaseRestClient supabase)
    {
        _supabase = supabase;
        var paceOptions = new PaceOptions();
        _paceCalculator = new DownhillPaceCalculator(paceOptions);
        _consistencyCalculator = new DownhillConsistencyCalculator(
            sessionWeights: paceOptions.SessionWeights);
        _resultScoreCalculator = new DownhillResultScoreCalculator(
            sessionWeights: paceOptions.SessionWeights);
    }

    internal IReadOnlyList<RiderSectorObservation> CalculateSimilarityObservations(
        IReadOnlyList<PacePrecomputedSessionData> sessions,
        IReadOnlyDictionary<string, string?> categoriesBySessionId)
    {
        return sessions
            .Where(session =>
                _paceCalculator.GetConfiguredSessionWeight(session.SessionType) > 0 &&
                !string.Equals(session.Run.Status, "DNS", StringComparison.OrdinalIgnoreCase) &&
                !DownhillPaceCalculator.IsDisqualified(session.Run.Status))
            .Select(session => new
            {
                Session = session,
                Result = _paceCalculator.CalculatePrecomputedSession(session)
            })
            .SelectMany(item => SimilarityObservations(
                item.Session.SessionId,
                item.Result,
                categoriesBySessionId.GetValueOrDefault(item.Session.SessionId)))
            .ToArray();
    }

    internal IReadOnlyList<RiderSectorObservation> CalculateSimilarityObservations(
        RankingDataBundle bundle,
        IReadOnlyDictionary<string, string?> categoriesBySessionId)
    {
        return CalculateSessionPaceObservations(bundle)
            .SelectMany(observation => SimilarityObservations(
                observation.SessionId,
                observation.Result,
                categoriesBySessionId.GetValueOrDefault(observation.SessionId)))
            .ToArray();
    }

    // Reuse the same run construction and incident detector for historical experiments.
    internal IReadOnlyList<RiderSessionPaceObservation> CalculateSessionPaceObservations(
        RankingDataBundle bundle)
    {
        var sessionsById = bundle.Sessions
            .Where(session => !string.IsNullOrWhiteSpace(session.String("id")))
            .ToDictionary(session => session.String("id")!, StringComparer.OrdinalIgnoreCase);
        var splitsByResultId = bundle.Splits
            .GroupBy(split => split.String("result_id") ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<JsonObject>)group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var fieldResults = bundle.FieldResults
            .Where(result => !DownhillPaceCalculator.IsDisqualified(result.String("status")))
            .ToArray();
        var splitLayouts = BuildSessionSplitLayouts(fieldResults, splitsByResultId);
        var observations = new List<RiderSessionPaceObservation>();

        foreach (var sessionGroup in fieldResults
                     .Where(result => !string.IsNullOrWhiteSpace(result.String("session_id")))
                     .GroupBy(result => result.String("session_id")!, StringComparer.OrdinalIgnoreCase))
        {
            sessionsById.TryGetValue(sessionGroup.Key, out var session);
            splitLayouts.TryGetValue(sessionGroup.Key, out var splitLayout);
            var runs = sessionGroup
                .Where(result => !string.IsNullOrWhiteSpace(result.String("rider_id")))
                .GroupBy(result => result.String("rider_id")!, StringComparer.OrdinalIgnoreCase)
                .Select(group => BuildRun(
                    SelectRun(group),
                    splitsByResultId,
                    splitLayout ?? SessionSplitLayout.Empty))
                .ToArray();
            var paceSession = new PaceSessionData(
                ParseSessionType(session?.String("session_type"), session?.String("name")),
                runs,
                CompetitionClassPriority(session));
            if (_paceCalculator.GetConfiguredSessionWeight(paceSession.SessionType) <= 0)
            {
                continue;
            }
            foreach (var result in _paceCalculator.CalculateSession(paceSession))
            {
                observations.Add(new RiderSessionPaceObservation(
                    sessionGroup.Key, result.RiderId, result));
            }
        }

        return observations;
    }

    internal IReadOnlyList<RiderSessionPaceObservation> CalculateSessionPaceObservations(
        IReadOnlyList<PacePrecomputedSessionData> sessions,
        string? riderId = null)
    {
        return sessions
            .Where(session =>
                (string.IsNullOrWhiteSpace(riderId) || string.Equals(
                    session.Run.RiderId,
                    riderId,
                    StringComparison.OrdinalIgnoreCase)) &&
                _paceCalculator.GetConfiguredSessionWeight(session.SessionType) > 0 &&
                !string.Equals(session.Run.Status, "DNS", StringComparison.OrdinalIgnoreCase) &&
                !DownhillPaceCalculator.IsDisqualified(session.Run.Status))
            .Select(session => new RiderSessionPaceObservation(
                session.SessionId,
                session.Run.RiderId,
                _paceCalculator.CalculatePrecomputedSession(session)))
            .Where(observation => observation.Result.Pace.HasValue)
            .ToArray();
    }

    internal double? CalculateStagePace(IEnumerable<RiderSessionPaceResult> sessions) =>
        _paceCalculator.CalculateStagePace(sessions)?.Pace;

    private static IEnumerable<RiderSectorObservation> SimilarityObservations(
        string sessionId,
        RiderSessionPaceResult result,
        string? categoryCode)
    {
        var sectorCount = result.Sectors.Count;
        return result.Sectors
            .Where(sector =>
                sector.Score.HasValue &&
                sector.Status is PaceSectorStatus.Valid or PaceSectorStatus.RecoveredAfterIncident)
            .Select(sector => new RiderSectorObservation(
                result.RiderId,
                sessionId,
                categoryCode,
                sector.SectorIndex,
                sectorCount,
                sector.Score!.Value));
    }

    public async Task<IReadOnlyDictionary<string, RiderPerformanceMetricValues>> CalculateAsync(
        IReadOnlyList<JsonObject> selectedResults,
        RiderMetricsContext context,
        CancellationToken cancellationToken = default,
        bool resultsContainFullField = false,
        IReadOnlyList<JsonObject>? availableSessions = null,
        IReadOnlyList<JsonObject>? suppliedSplits = null,
        IReadOnlyList<JsonObject>? suppliedFieldResults = null)
    {
        var selectedResultsByRiderId = selectedResults
            .Where(result => !string.IsNullOrWhiteSpace(result.String("rider_id")))
            .GroupBy(result => result.String("rider_id")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<JsonObject>)group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var targetRiderIds = selectedResultsByRiderId.Keys.ToArray();

        if (targetRiderIds.Length == 0)
        {
            return new Dictionary<string, RiderPerformanceMetricValues>(StringComparer.OrdinalIgnoreCase);
        }

        var sessionsById = selectedResults
            .Select(result => result.Object("sessions"))
            .Where(session => session?.String("id") is not null)
            .Select(session => session!)
            .GroupBy(session => session.String("id")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var sessionIds = selectedResults
            .Select(ResultSessionId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (sessionIds.Length == 0)
        {
            return EmptyPace(targetRiderIds);
        }

        var fetchedFieldResults = suppliedFieldResults ?? (resultsContainFullField
            ? selectedResults
            : await FetchFieldResultsAsync(sessionIds, context.Status, cancellationToken));
        var fieldResults = fetchedFieldResults
            .Where(result => !DownhillPaceCalculator.IsDisqualified(result.String("status")))
            .ToArray();

        if (fieldResults.Length == 0)
        {
            return EmptyPace(targetRiderIds);
        }

        var resultIds = fieldResults
            .Select(result => result.String("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var splits = suppliedSplits ?? await FetchSplitsAsync(resultIds, cancellationToken);
        var splitsByResultId = splits
            .GroupBy(split => split.String("result_id") ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<JsonObject>)group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var splitLayoutsBySessionId = BuildSessionSplitLayouts(
            fieldResults,
            splitsByResultId);

        var sessionGroups = fieldResults
            .GroupBy(
                result => result.String("session_id") ?? string.Empty,
                StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .ToArray();
        var classifiedFieldSizesBySessionId = fieldResults
            .Where(IsClassifiedRun)
            .Where(result => !string.IsNullOrWhiteSpace(result.String("rider_id")))
            .GroupBy(
                result => result.String("session_id") ?? string.Empty,
                StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(result => result.String("rider_id")!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(),
                StringComparer.OrdinalIgnoreCase);
        var paceSessions = new PaceSessionData[sessionGroups.Length];
        var paceResultsBySession = new IReadOnlyList<RiderSessionPaceResult>[sessionGroups.Length];

        Parallel.For(0, sessionGroups.Length, sessionIndex =>
        {
            var sessionGroup = sessionGroups[sessionIndex];
            sessionsById.TryGetValue(sessionGroup.Key, out var session);
            splitLayoutsBySessionId.TryGetValue(
                sessionGroup.Key,
                out var splitLayout);
            var runs = sessionGroup
                .Where(result => !string.IsNullOrWhiteSpace(result.String("rider_id")))
                .GroupBy(result => result.String("rider_id")!, StringComparer.OrdinalIgnoreCase)
                .Select(group => BuildRun(
                    SelectRun(group),
                    splitsByResultId,
                    splitLayout ?? SessionSplitLayout.Empty))
                .ToArray();

            var paceSession = new PaceSessionData(
                ParseSessionType(session?.String("session_type"), session?.String("name")),
                runs,
                CompetitionClassPriority(session));
            paceSessions[sessionIndex] = paceSession;
            paceResultsBySession[sessionIndex] = _paceCalculator.CalculateSession(paceSession);
        });

        var sessionPaceResults = paceResultsBySession
            .SelectMany(results => results)
            .ToArray();
        var sessionPaceResultBySessionAndRider = Enumerable
            .Range(0, sessionGroups.Length)
            .SelectMany(sessionIndex => paceResultsBySession[sessionIndex]
                .Select(result => new
                {
                    Key = ConsistencyPaceKey(
                        sessionGroups[sessionIndex].Key,
                        result.RiderId),
                    Result = result
                }))
            .ToDictionary(
                item => item.Key,
                item => item.Result,
                StringComparer.OrdinalIgnoreCase);
        var sessionPaceResultsByRiderId = sessionPaceResults
            .GroupBy(result => result.RiderId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<RiderSessionPaceResult>)group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var paceSessionBySessionId = Enumerable.Range(0, sessionGroups.Length)
            .ToDictionary(
                index => sessionGroups[index].Key,
                index => paceSessions[index],
                StringComparer.OrdinalIgnoreCase);
        var paceSessionsByRiderId = selectedResultsByRiderId.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<PaceSessionData>)pair.Value
                .Select(ResultSessionId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(paceSessionBySessionId.ContainsKey)
                .Select(id => paceSessionBySessionId[id])
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);
        var availableFinalsByRiderId = BuildAvailableFinalCounts(
            targetRiderIds,
            selectedResultsByRiderId,
            availableSessions);
        var nonFinalistsByRiderId = BuildNonFinalistFlags(
            targetRiderIds,
            selectedResultsByRiderId,
            availableSessions);
        var competitionClassWeighting = BuildCompetitionClassWeighting(
            availableSessions,
            sessionsById.Values);
        var metrics = new RiderPerformanceMetricValues[targetRiderIds.Length];
        Parallel.For(0, targetRiderIds.Length, riderIndex =>
        {
            var riderId = targetRiderIds[riderIndex];
            availableFinalsByRiderId.TryGetValue(riderId, out var availableFinals);
            var isNonFinalist = nonFinalistsByRiderId.GetValueOrDefault(riderId);
            var riderResults = selectedResultsByRiderId[riderId];
            var riderSessionPaceResults = sessionPaceResultsByRiderId.GetValueOrDefault(
                riderId,
                Array.Empty<RiderSessionPaceResult>());
            var riderPaceSessions = paceSessionsByRiderId.GetValueOrDefault(
                riderId,
                Array.Empty<PaceSessionData>());
            var consistencyRuns = BuildConsistencyRuns(
                riderId,
                riderResults,
                splitsByResultId,
                splitLayoutsBySessionId,
                classifiedFieldSizesBySessionId,
                sessionPaceResultBySessionAndRider);
            metrics[riderIndex] = ToMetric(
                _paceCalculator.CalculatePace(
                    riderId,
                    riderSessionPaceResults,
                    availableFinals),
                _consistencyCalculator.Calculate(consistencyRuns),
                _resultScoreCalculator.Calculate(riderId, riderPaceSessions),
                _paceCalculator.CalculatePace(
                    riderId,
                    riderSessionPaceResults,
                    availableFinals,
                    competitionClassWeighting),
                _consistencyCalculator.Calculate(
                    consistencyRuns,
                    competitionClassWeighting),
                _resultScoreCalculator.Calculate(
                    riderId,
                    riderPaceSessions,
                    competitionClassWeighting),
                isNonFinalist);
        });

        return Enumerable.Range(0, targetRiderIds.Length).ToDictionary(
            index => targetRiderIds[index],
            index => metrics[index],
            StringComparer.OrdinalIgnoreCase);
    }

    internal IReadOnlyDictionary<string, RiderPerformanceMetricValues> CalculatePrecomputed(
        IReadOnlyList<JsonObject> selectedResults,
        IReadOnlyList<PacePrecomputedSessionData> precomputedSessions,
        IReadOnlyList<JsonObject>? availableSessions = null)
    {
        var resultsByRiderId = selectedResults
            .Where(result => !string.IsNullOrWhiteSpace(result.String("rider_id")))
            .GroupBy(result => result.String("rider_id")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<JsonObject>)group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        if (resultsByRiderId.Count == 0)
        {
            return new Dictionary<string, RiderPerformanceMetricValues>(StringComparer.OrdinalIgnoreCase);
        }

        var selectedSessionRiderKeys = selectedResults
            .Where(result =>
                !DownhillPaceCalculator.IsDisqualified(result.String("status")) &&
                !string.IsNullOrWhiteSpace(result.String("rider_id")) &&
                !string.IsNullOrWhiteSpace(ResultSessionId(result)))
            .Select(result => ConsistencyPaceKey(
                ResultSessionId(result)!,
                result.String("rider_id")!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var applicableSessions = precomputedSessions
            .Where(session =>
                !DownhillPaceCalculator.IsDisqualified(session.Run.Status) &&
                selectedSessionRiderKeys.Contains(
                ConsistencyPaceKey(session.SessionId, session.Run.RiderId)))
            .ToArray();
        var sessionsByRiderId = applicableSessions
            .GroupBy(session => session.Run.RiderId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<PacePrecomputedSessionData>)group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var availableFinalsByRiderId = BuildAvailableFinalCounts(
            resultsByRiderId.Keys.ToArray(),
            resultsByRiderId,
            availableSessions);
        var nonFinalistsByRiderId = BuildNonFinalistFlags(
            resultsByRiderId.Keys.ToArray(),
            resultsByRiderId,
            availableSessions);
        var competitionClassWeighting = BuildCompetitionClassWeighting(
            availableSessions,
            selectedResults
                .Select(result => result.Object("sessions"))
                .Where(session => session is not null)
                .Select(session => session!));

        var output = new Dictionary<string, RiderPerformanceMetricValues>(StringComparer.OrdinalIgnoreCase);
        foreach (var (riderId, riderResults) in resultsByRiderId)
        {
            var riderSessions = sessionsByRiderId.GetValueOrDefault(
                riderId,
                Array.Empty<PacePrecomputedSessionData>());
            var sessionPaceResults = riderSessions
                .Select(_paceCalculator.CalculatePrecomputedSession)
                .ToArray();
            var consistencyRuns = riderSessions
                .Zip(sessionPaceResults)
                .Select(pair =>
                {
                    var outlierProfile = _consistencyCalculator.AnalyzeSectors(pair.Second.Sectors);
                    return new ConsistencyRunInput(
                        pair.First.Run.RiderId + ":" + pair.First.SessionId,
                        pair.First.CourseKey,
                        pair.First.SessionType,
                        pair.Second.Sectors
                            .Select((sector, index) => new ConsistencySectorTimeInput(
                                $"sector:{index}",
                                sector.Time,
                                outlierProfile.SectorIndexes.Contains(index)))
                            .ToArray(),
                        FinishRank: pair.First.Run.FinishRank,
                        FieldSize: pair.First.ClassifiedFieldSize > 0
                            ? pair.First.ClassifiedFieldSize
                            : null,
                        IsClassified: pair.First.Run.IsClassified,
                        CompetitionClassPriority: pair.First.CompetitionClassPriority,
                        ProjectedFinishPositionScore: outlierProfile.ProjectedFinishPositionScore,
                        Status: pair.First.Run.Status,
                        HasIncident: pair.Second.IncidentSectorIndex.HasValue);
                })
                .ToArray();
            availableFinalsByRiderId.TryGetValue(riderId, out var availableFinals);
            var isNonFinalist = nonFinalistsByRiderId.GetValueOrDefault(riderId);

            output[riderId] = ToMetric(
                _paceCalculator.CalculatePace(
                    riderId,
                    sessionPaceResults,
                    availableFinals),
                _consistencyCalculator.Calculate(consistencyRuns),
                _resultScoreCalculator.Calculate(riderSessions),
                _paceCalculator.CalculatePace(
                    riderId,
                    sessionPaceResults,
                    availableFinals,
                    competitionClassWeighting),
                _consistencyCalculator.Calculate(
                    consistencyRuns,
                    competitionClassWeighting),
                _resultScoreCalculator.Calculate(
                    riderSessions,
                    competitionClassWeighting),
                isNonFinalist);
        }

        return output;
    }

    private static CompetitionClassWeighting BuildCompetitionClassWeighting(
        IReadOnlyList<JsonObject>? availableSessions,
        IEnumerable<JsonObject> resultSessions)
    {
        var priorities = availableSessions?
            .Select(CompetitionClassPriority)
            .Where(priority => priority.HasValue)
            .ToArray() ?? [];
        if (priorities.Length == 0)
        {
            priorities = resultSessions
                .Select(CompetitionClassPriority)
                .Where(priority => priority.HasValue)
                .ToArray();
        }

        return CompetitionClassWeighting.FromPriorities(priorities);
    }

    private static int? CompetitionClassPriority(JsonObject? session)
    {
        var round = session?.Object("rounds") ??
            session?.Object("round_categories")?.Object("rounds");
        return round?.Int("competition_class_priority") ??
            round?.Object("competition_class")?.Int("priority") ??
            round?.Object("competition_levels")?.Int("priority");
    }

    private static IReadOnlyDictionary<string, int?> BuildAvailableFinalCounts(
        IReadOnlyList<string> riderIds,
        IReadOnlyDictionary<string, IReadOnlyList<JsonObject>> selectedResultsByRiderId,
        IReadOnlyList<JsonObject>? availableSessions)
    {
        if (availableSessions is null)
        {
            return riderIds.ToDictionary(
                riderId => riderId,
                _ => (int?)null,
                StringComparer.OrdinalIgnoreCase);
        }

        var availableFinals = availableSessions
            .Where(DomainQueryService.IsFinalSession)
            .GroupBy(session => session.String("id") ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group => group.First())
            .ToArray();

        return riderIds.ToDictionary(
            riderId => riderId,
            riderId =>
            {
                var categoryIds = selectedResultsByRiderId
                    .GetValueOrDefault(riderId, Array.Empty<JsonObject>())
                    .Select(ResultCategoryId)
                    .Where(categoryId => !string.IsNullOrWhiteSpace(categoryId))
                    .Select(categoryId => categoryId!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                return (int?)(categoryIds.Count == 0
                    ? availableFinals.Length
                    : availableFinals.Count(session =>
                        categoryIds.Contains(session.String("category_id") ?? string.Empty)));
            },
            StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, bool> BuildNonFinalistFlags(
        IReadOnlyList<string> riderIds,
        IReadOnlyDictionary<string, IReadOnlyList<JsonObject>> selectedResultsByRiderId,
        IReadOnlyList<JsonObject>? availableSessions)
    {
        return riderIds.ToDictionary(
            riderId => riderId,
            riderId => IsNonFinalist(
                selectedResultsByRiderId.GetValueOrDefault(
                    riderId,
                    Array.Empty<JsonObject>()),
                availableSessions),
            StringComparer.OrdinalIgnoreCase);
    }

    internal static bool IsNonFinalist(
        IReadOnlyList<JsonObject> riderResults,
        IReadOnlyList<JsonObject>? availableSessions)
    {
        if (availableSessions is null)
        {
            return false;
        }

        var roundCategoryKeys = riderResults
            .Select(ResultRoundCategoryKey)
            .Where(key => key is not null)
            .Select(key => key!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (roundCategoryKeys.Length != 1)
        {
            return false;
        }

        var key = roundCategoryKeys[0];
        var completedQualifying = riderResults.Any(result =>
        {
            var session = result.Object("sessions");
            var sessionType = ParseSessionType(
                session?.String("session_type"),
                session?.String("name"));
            var status = result.String("status")?.Trim();
            return DownhillPaceCalculator.IsQualifyingSessionType(sessionType) &&
                !DownhillPaceCalculator.IsDisqualified(status) &&
                !string.Equals(status, "DNS", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(status, "DNF", StringComparison.OrdinalIgnoreCase) &&
                result.Int("position") is > 0;
        });
        if (!completedQualifying)
        {
            return false;
        }

        // Any final row, including DNS/DNF/DSQ, confirms that the rider advanced.
        var hasFinalEntry = riderResults.Any(result =>
        {
            var session = result.Object("sessions");
            return session is not null &&
                DomainQueryService.IsFinalSession(session) &&
                string.Equals(ResultRoundCategoryKey(result), key, StringComparison.OrdinalIgnoreCase);
        });
        if (hasFinalEntry)
        {
            return false;
        }

        return availableSessions.Any(session =>
            DomainQueryService.IsFinalSession(session) &&
            string.Equals(SessionRoundCategoryKey(session), key, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ResultRoundCategoryKey(JsonObject result)
    {
        var session = result.Object("sessions");
        return session is null ? null : SessionRoundCategoryKey(session);
    }

    private static string? SessionRoundCategoryKey(JsonObject session)
    {
        var roundId = session.String("round_id") ??
            session.Object("rounds")?.String("id") ??
            session.Object("round_categories")?.String("round_id") ??
            session.Object("round_categories")?.Object("rounds")?.String("id");
        var categoryId = session.String("category_id") ??
            session.Object("categories")?.String("id") ??
            session.Object("round_categories")?.String("category_id") ??
            session.Object("round_categories")?.Object("categories")?.String("id");
        return string.IsNullOrWhiteSpace(roundId) || string.IsNullOrWhiteSpace(categoryId)
            ? null
            : $"{roundId}\u001f{categoryId}";
    }

    private static string? ResultCategoryId(JsonObject result)
    {
        var session = result.Object("sessions");
        return session?.String("category_id") ??
            session?.Object("categories")?.String("id") ??
            session?.Object("round_categories")?.String("category_id") ??
            session?.Object("round_categories")?.Object("categories")?.String("id");
    }

    private async Task<IReadOnlyList<JsonObject>> FetchFieldResultsAsync(
        IReadOnlyList<string> sessionIds,
        string? status,
        CancellationToken cancellationToken)
    {
        var rows = new List<JsonObject>();
        foreach (var batch in sessionIds.Chunk(SessionIdBatchSize))
        {
            rows.AddRange(await FetchAllAsync(
                (limit, offset) =>
                {
                    var query = new SupabaseQuery("results")
                        .Select("id,rider_id,session_id,race_time_ms,top_speed,position,is_classified,status")
                        .In("session_id", batch)
                        .Order("id", descending: false)
                        .Limit(limit)
                        .Offset(offset);
                    query.Filter("status", "eq", status);
                    return query;
                },
                cancellationToken));
        }

        return rows;
    }

    private async Task<IReadOnlyList<JsonObject>> FetchSplitsAsync(
        IReadOnlyList<string> resultIds,
        CancellationToken cancellationToken)
    {
        var batches = resultIds
            .Chunk(ResultIdBatchSize)
            .Select(batch => batch.ToArray())
            .ToArray();
        var rowsByBatch = new IReadOnlyList<JsonObject>[batches.Length];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, batches.Length),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaximumConcurrentSplitQueries,
                CancellationToken = cancellationToken
            },
            async (batchIndex, token) =>
            {
                var batch = batches[batchIndex];
                rowsByBatch[batchIndex] = await FetchAllAsync(
                    (limit, offset) => new SupabaseQuery("time_splits")
                        .Select("id,result_id,split_index,race_time")
                        .In("result_id", batch)
                        .Order("id", descending: false)
                        .Limit(limit)
                        .Offset(offset),
                    token);
            });

        return rowsByBatch.SelectMany(rows => rows).ToArray();
    }

    private async Task<IReadOnlyList<JsonObject>> FetchAllAsync(
        Func<int, int, SupabaseQuery> createQuery,
        CancellationToken cancellationToken)
    {
        var rows = new List<JsonObject>();
        var offset = 0;

        while (true)
        {
            var response = await _supabase.GetAsync(
                createQuery(PageSize, offset),
                count: false,
                cancellationToken: cancellationToken);
            rows.AddRange(response.Data);

            if (response.Data.Count == 0 ||
                response.Data.Count < PageSize)
            {
                break;
            }

            offset += response.Data.Count;
        }

        return rows;
    }

    private static JsonObject SelectRun(IEnumerable<JsonObject> duplicates)
    {
        return duplicates
            .OrderByDescending(result => result.Decimal("id") ?? 0)
            .First();
    }

    private static PaceRiderRun BuildRun(
        JsonObject result,
        IReadOnlyDictionary<string, IReadOnlyList<JsonObject>> splitsByResultId,
        SessionSplitLayout splitLayout)
    {
        var resultId = result.String("id") ?? string.Empty;
        splitsByResultId.TryGetValue(resultId, out var resultSplits);
        var sectorTimes = BuildSectorTimes(
            resultSplits ?? [],
            splitLayout.MaximumSplitIndex,
            result.Int("race_time_ms"));
        var finishTimeMs = result.Int("race_time_ms");

        return new PaceRiderRun(
            RiderId: result.String("rider_id")!,
            SectorTimes: sectorTimes,
            TopSpeed: result.Decimal("top_speed") is { } speed ? (double)speed : null,
            FinishRank: result.Int("position"),
            FinishTimeSeconds: finishTimeMs is > 0
                ? finishTimeMs.Value / 1000.0
                : null,
            IsClassified: IsClassifiedRun(result),
            Status: result.String("status"));
    }

    private static bool IsClassifiedRun(JsonObject result)
    {
        var status = result.String("status");
        return (result.Bool("is_classified") == true || result.Int("position") is > 0) &&
            !string.Equals(status, "DNF", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "DNS", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "DSQ", StringComparison.OrdinalIgnoreCase);
    }

    private IReadOnlyList<ConsistencyRunInput> BuildConsistencyRuns(
        string riderId,
        IReadOnlyList<JsonObject> riderResults,
        IReadOnlyDictionary<string, IReadOnlyList<JsonObject>> splitsByResultId,
        IReadOnlyDictionary<string, SessionSplitLayout> splitLayoutsBySessionId,
        IReadOnlyDictionary<string, int> classifiedFieldSizesBySessionId,
        IReadOnlyDictionary<string, RiderSessionPaceResult> sessionPaceResultsByRider)
    {
        var runs = riderResults
            .Where(result => !string.IsNullOrWhiteSpace(result.String("id")))
            .GroupBy(result => result.String("id")!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(result =>
            {
                var resultId = result.String("id")!;
                var sessionId = ResultSessionId(result) ?? string.Empty;
                splitsByResultId.TryGetValue(resultId, out var resultSplits);
                splitLayoutsBySessionId.TryGetValue(sessionId, out var splitLayout);
                classifiedFieldSizesBySessionId.TryGetValue(
                    sessionId,
                    out var fieldSize);
                sessionPaceResultsByRider.TryGetValue(
                    ConsistencyPaceKey(sessionId, riderId),
                    out var paceResult);
                var outlierProfile = paceResult is null
                    ? new ConsistencyOutlierProfile(new HashSet<int>(), null)
                    : _consistencyCalculator.AnalyzeSectors(paceResult.Sectors);
                var sectorTimes = paceResult is null
                    ? BuildSectorTimes(
                        resultSplits ?? [],
                        splitLayout?.MaximumSplitIndex ?? 0,
                        result.Int("race_time_ms"))
                    : paceResult.Sectors.Select(sector => sector.Time).ToArray();
                var sectors = sectorTimes
                    .Select((time, index) => new ConsistencySectorTimeInput(
                        $"sector:{index}",
                        time,
                        outlierProfile.SectorIndexes.Contains(index)))
                    .ToArray();

                return new ConsistencyRunInput(
                    resultId,
                    BuildCourseKey(result),
                    ParseSessionType(
                        result.Object("sessions")?.String("session_type"),
                        result.Object("sessions")?.String("name")),
                    sectors,
                    FinishRank: result.Int("position"),
                    FieldSize: fieldSize > 0 ? fieldSize : null,
                    IsClassified: IsClassifiedRun(result),
                    CompetitionClassPriority: CompetitionClassPriority(
                        result.Object("sessions")),
                    ProjectedFinishPositionScore: outlierProfile.ProjectedFinishPositionScore,
                    Status: result.String("status"),
                    HasIncident: paceResult?.IncidentSectorIndex.HasValue == true);
            })
            .ToArray();

        return runs;
    }

    private static string ConsistencyPaceKey(
        string sessionId,
        string riderId)
    {
        return $"{sessionId}\u001f{riderId}";
    }

    internal static IReadOnlyList<double?> BuildSectorTimes(
        IReadOnlyList<JsonObject> splits,
        int maximumSplitIndex,
        int? finishTimeMs = null)
    {
        var latestSplitsByIndex = splits
            .Where(split => split.Int("split_index") is > 0)
            .GroupBy(split => split.Int("split_index")!.Value)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(split => split.Decimal("id") ?? 0)
                    .First());
        maximumSplitIndex = Math.Max(
            maximumSplitIndex,
            latestSplitsByIndex.Keys.DefaultIfEmpty(0).Max());

        var sectors = new List<double?>(maximumSplitIndex);
        int? previousTimeMs = 0;
        for (var splitIndex = 1; splitIndex <= maximumSplitIndex; splitIndex++)
        {
            var cumulativeTimeMs = latestSplitsByIndex
                .GetValueOrDefault(splitIndex)?
                .Int("race_time");
            if (cumulativeTimeMs is > 0 &&
                previousTimeMs.HasValue &&
                cumulativeTimeMs.Value > previousTimeMs.Value)
            {
                sectors.Add(
                    (cumulativeTimeMs.Value - previousTimeMs.Value) / 1000.0);
            }
            else
            {
                sectors.Add(null);
            }

            previousTimeMs = cumulativeTimeMs is > 0
                ? cumulativeTimeMs.Value
                : null;
        }

        if (finishTimeMs is > 0 &&
            previousTimeMs is > 0 &&
            finishTimeMs.Value > previousTimeMs.Value)
        {
            sectors.Add((finishTimeMs.Value - previousTimeMs.Value) / 1000.0);
        }

        return sectors;
    }

    private static IReadOnlyDictionary<string, SessionSplitLayout> BuildSessionSplitLayouts(
        IReadOnlyList<JsonObject> fieldResults,
        IReadOnlyDictionary<string, IReadOnlyList<JsonObject>> splitsByResultId)
    {
        return fieldResults
            .Where(result => !string.IsNullOrWhiteSpace(result.String("session_id")))
            .GroupBy(
                result => result.String("session_id")!,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var resultSplits = group
                        .SelectMany(result => splitsByResultId.GetValueOrDefault(
                            result.String("id") ?? string.Empty,
                            Array.Empty<JsonObject>()))
                        .ToArray();
                    var maximumIndex = resultSplits
                        .Select(split => split.Int("split_index") ?? 0)
                        .DefaultIfEmpty(0)
                        .Max();
                    return new SessionSplitLayout(maximumIndex);
                },
                StringComparer.OrdinalIgnoreCase);
    }

    private sealed record SessionSplitLayout(int MaximumSplitIndex)
    {
        public static SessionSplitLayout Empty { get; } = new(0);
    }

    internal static PaceSessionType ParseSessionType(string? sessionType, string? sessionName)
    {
        var type = DomainQueryService.NormalizeSessionType(sessionType ?? string.Empty);
        var name = (sessionName ?? string.Empty).Trim().ToUpperInvariant();

        if (type == "SEMI" || name.Contains("SEMI", StringComparison.Ordinal))
        {
            return PaceSessionType.SemiFinal;
        }

        if (type == "FINAL" || name.Contains("FINAL", StringComparison.Ordinal))
        {
            return PaceSessionType.Final;
        }

        if (type == "Q1" || name == "Q1" || name.Contains("QUALIFYING 1", StringComparison.Ordinal))
        {
            return PaceSessionType.Qualifying1;
        }

        if (type == "Q2" || name == "Q2" || name.Contains("QUALIFYING 2", StringComparison.Ordinal))
        {
            return PaceSessionType.Qualifying2;
        }

        if (type is "QUALI" or "QUALIFYING" or "QUALIFICATION" ||
            name.Contains("QUAL", StringComparison.Ordinal))
        {
            return PaceSessionType.Qualifying;
        }

        if (type == "SEEDING" || name.Contains("SEED", StringComparison.Ordinal))
        {
            return PaceSessionType.Seeding;
        }

        if (type == "TIMED_TRAINING" ||
            (name.Contains("TIMED", StringComparison.Ordinal) &&
             name.Contains("TRAIN", StringComparison.Ordinal)))
        {
            return PaceSessionType.TimedTraining;
        }

        return PaceSessionType.Training;
    }

    internal static string BuildCourseKey(JsonObject result)
    {
        var session = result.Object("sessions");
        var round = session?.Object("rounds") ?? session?.Object("round_categories")?.Object("rounds");
        var category = session?.Object("categories") ?? session?.Object("round_categories")?.Object("categories");
        var roundId = session?.String("round_id") ?? round?.String("id");
        var categoryId = session?.String("category_id") ?? category?.String("id");

        if (!string.IsNullOrWhiteSpace(roundId) && !string.IsNullOrWhiteSpace(categoryId))
        {
            return $"round:{roundId}:category:{categoryId}";
        }

        var trackId = round?.String("track_id") ?? round?.Object("tracks")?.String("id");
        var seasonYear = round?.String("season_year");
        if (!string.IsNullOrWhiteSpace(trackId) &&
            !string.IsNullOrWhiteSpace(seasonYear) &&
            !string.IsNullOrWhiteSpace(categoryId))
        {
            return $"track:{trackId}:season:{seasonYear}:category:{categoryId}";
        }

        return $"session:{ResultSessionId(result) ?? result.String("id") ?? "unknown"}";
    }

    private RiderPerformanceMetricValues ToMetric(
        RiderPaceResult pace,
        RiderConsistencyResult consistency,
        RiderResultScore resultScore,
        RiderPaceResult ratingPace,
        RiderConsistencyResult ratingConsistency,
        RiderResultScore ratingResultScore,
        bool isNonFinalist = false)
    {
        var paceValue = pace.Pace.HasValue
            ? Math.Round((decimal)pace.Pace.Value, 2)
            : (decimal?)null;
        var rawPaceValue = pace.RawPace.HasValue
            ? Math.Round((decimal)pace.RawPace.Value, 2)
            : (decimal?)null;
        var consistencyValue = consistency.Consistency.HasValue
            ? Math.Round((decimal)consistency.Consistency.Value, 2)
            : (decimal?)null;
        var resultScoreValue = resultScore.Score.HasValue
            ? Math.Round((decimal)resultScore.Score.Value, 2)
            : (decimal?)null;
        var ratingResult = ratingPace.Pace.HasValue && ratingConsistency.Consistency.HasValue
            ? _ratingCalculator.Calculate(
                ratingPace.Pace.Value,
                ratingConsistency.Consistency.Value,
                ratingResultScore.Score)
            : null;
        var rating = ratingResult is null
            ? (decimal?)null
            : Math.Round((decimal)_paceCalculator.ApplyNonFinalistScoreCap(
                ratingResult.Rating,
                isNonFinalist), 2);
        var rawRating = ratingResult is null
            ? (decimal?)null
            : Math.Round((decimal)_paceCalculator.ApplyNonFinalistScoreCap(
                ratingResult.RawRating,
                isNonFinalist), 2);

        return new RiderPerformanceMetricValues(
            rating,
            rawRating,
            paceValue,
            rawPaceValue,
            Math.Round((decimal)pace.Confidence, 4),
            pace.UsedSessions,
            consistencyValue,
            Math.Round((decimal)consistency.Confidence, 4),
            consistency.UsedRuns,
            consistency.UsedSectors)
        {
            PaceConfidenceStatus = pace.ConfidenceStatus,
            PaceUsedFinals = pace.UsedFinals,
            PaceTotalEvidence = Math.Round((decimal)pace.TotalEvidence, 4),
            PaceFinalEvidence = Math.Round((decimal)pace.FinalEvidence, 4),
            ResultScore = resultScoreValue,
            ResultScoreConfidence = Math.Round((decimal)resultScore.Confidence, 4),
            ResultScoreUsedSessions = resultScore.UsedSessions
        };
    }

    private static IReadOnlyDictionary<string, RiderPerformanceMetricValues> EmptyPace(
        IEnumerable<string> riderIds)
    {
        return riderIds.ToDictionary(
            riderId => riderId,
            _ => new RiderPerformanceMetricValues(null, null, null, null, 0, 0, null, 0, 0, 0),
            StringComparer.OrdinalIgnoreCase);
    }

    private static string? ResultSessionId(JsonObject result)
    {
        return result.String("session_id") ?? result.Object("sessions")?.String("id");
    }

}
