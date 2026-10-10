using RaceTrace.Api.Infrastructure.Calculations;
using RaceTrace.Api.Infrastructure.Telemetry;

namespace RaceTrace.Api.Services;

internal enum PaceSessionType
{
    Final,
    SemiFinal,
    Qualifying1,
    Qualifying2,
    Qualifying,
    Seeding,
    TimedTraining,
    Training
}

internal sealed record PaceRiderRun(
    string RiderId,
    IReadOnlyList<double?> SectorTimes,
    double? TopSpeed = null,
    int? FinishRank = null,
    double? FinishTimeSeconds = null,
    double? HistoricalExpectedPercentile = null,
    bool IsClassified = true,
    string? Status = null);

internal sealed record PaceSessionData(
    PaceSessionType SessionType,
    IReadOnlyList<PaceRiderRun> Runs,
    int? CompetitionClassPriority = null);

internal sealed record PaceFieldSummary(
    int Count,
    double Median,
    double LogMedian,
    double LogMadScale,
    double LinearMadScale,
    double? BestValue = null,
    double? SecondBestValue = null);

internal sealed record PacePrecomputedSector(
    int SectorIndex,
    double? TimeSeconds,
    int? Rank,
    PaceFieldSummary Statistics);

internal sealed record PacePrecomputedSessionData(
    string SessionId,
    string CourseKey,
    string? CategoryId,
    PaceSessionType SessionType,
    PaceRiderRun Run,
    int ClassifiedFieldSize,
    IReadOnlyList<PacePrecomputedSector> Sectors,
    PaceFieldSummary? SpeedStatistics,
    int? SpeedRank,
    PaceFieldSummary? FinishStatistics,
    IReadOnlyList<PaceFieldSummary?> CumulativeStatistics,
    int? CompetitionClassPriority = null,
    DateTimeOffset? SessionDate = null)
{
    // Prepared for the complete session/category, before selecting any rider.
    public PaceFinishReference? PaceFinish { get; init; }
}

internal sealed record PaceFinishReference(double? TimeSeconds, double RemovedLossSeconds, PaceFieldSummary Statistics);

internal enum PaceSectorStatus
{
    Valid,
    Incident,
    RecoveredAfterIncident,
    AfterIncident,
    Missing,
    InvalidTiming,
    PossibleIncident
}

internal sealed record PaceSectorResult(
    int SectorIndex,
    PaceSectorStatus Status,
    double? Time,
    int? Rank,
    double? Percentile,
    double? PerformanceZ,
    double? Score)
{
    public SectorAnomaly? Anomaly { get; init; }
}

internal sealed record RiderSessionPaceResult(
    string RiderId,
    PaceSessionType SessionType,
    double? Pace,
    double? SectorPace,
    double? SpeedScore,
    double? FullRunTimeScore,
    double Confidence,
    double SessionWeight,
    bool IsRepresentative,
    int? IncidentSectorIndex,
    IReadOnlyList<PaceSectorResult> Sectors,
    double SectorCoverage = 1.0,
    double? EvidenceAdjustedSectorPace = null,
    double DominanceBonus = 0,
    string? NoResultReason = null,
    int? CompetitionClassPriority = null,
    DateTimeOffset? SessionDate = null);

internal sealed record RiderSessionPaceObservation(
    string SessionId,
    string RiderId,
    RiderSessionPaceResult Result);

internal sealed record RiderPaceResult(
    string RiderId,
    double? Pace,
    double? RawPace,
    double Confidence,
    int UsedSessions,
    int UsedFinals = 0,
    double TotalEvidence = 0,
    double FinalEvidence = 0,
    string ConfidenceStatus = "Low");

internal sealed record WeightedScore(double Score, double Weight);

internal sealed record WeightedPace(double Pace, double Weight);

internal sealed record StagePaceResult(
    double Pace,
    double EvidenceWeight,
    double RoundWeight,
    int UsedSessions);



internal sealed class DownhillPaceCalculator
{
    private readonly PaceOptions _options;

    public DownhillPaceCalculator(PaceOptions? options = null)
    {
		_options = options ?? AlgorithmConfiguration.Pace;

        if (_options.NonFinalistMaxScore is < 0 or > 100 ||
            _options.SessionWeights.FinalTargetImpact is < 0 or > 1 ||
            Enum.GetValues<PaceSessionType>()
                .Select(_options.SessionWeights.GetWeight)
                .Any(weight => weight < 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Session weights must be non-negative, FinalTargetImpact must be between 0 and 1, " +
                "and NonFinalistMaxScore must be between 0 and 100.");
        }

		double maximumRawSectorScore =
			100.0 * (_options.RankWeight + _options.TimeWeight)
			+ _options.MaximumWinBonus;

		if (_options.RankWeight < 0 ||
			_options.RankWeight > 1 ||
			_options.TimeWeight < 0 ||
			_options.TimeWeight > 1 ||
			_options.MaximumWinBonus < 0 ||
			maximumRawSectorScore > 100.000001)
		{
			throw new ArgumentOutOfRangeException(
				nameof(options),
				"RankWeight and TimeWeight must be between 0 and 1, " +
				"MaximumWinBonus must be non-negative, and the maximum " +
				"raw sector score cannot exceed 100.");
		}

		if (_options.StrongSectorCalibrationThreshold is < 0 or >= 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "StrongSectorCalibrationThreshold must be between 0 and 100.");
        }

        if (_options.MinimumValidSectorsForSessionPace < 1 ||
            _options.FullEvidenceSectorCount <= 0 ||
            _options.CoverageWeightPower < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Pace evidence options must be positive.");
        }

        if (_options.FullConfidenceSectorCount <= 0 ||
            _options.CoverageConfidencePower < 0 ||
            _options.FullConfidenceFieldSize <= 0 ||
            _options.OverallEvidenceScale <= 0 ||
            _options.MinimumConfidenceWithoutFinals is < 0 or > 100 ||
            _options.FinalEvidenceScale <= 0 ||
            _options.FullFinalConfidenceEvidence is <= 0 or > 1 ||
            _options.FinalParticipationConfidencePower <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Confidence options are outside their valid ranges.");
        }

        if (_options.PostIncidentMinimumPositiveScore is < 0 or > 100 ||
            !double.IsFinite(_options.PostIncidentMinimumPerformanceZ) ||
            _options.PostIncidentMaximumScoreDrop < 0 ||
            _options.PostIncidentMaximumPercentileDrop is < 0 or > 1 ||
            _options.PostIncidentMaximumPerformanceDrop < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Post-incident recovery options are outside their valid ranges.");
        }

        if (_options.PaceCalibrationThreshold is < 0 or >= 100 ||
            _options.PaceCalibrationPower <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Final Pace calibration options are outside their valid ranges.");
        }

        if (_options.UpperPaceCalibrationMaximumBoost < 0 ||
            _options.UpperPaceCalibrationStart is < 0 or > 100 ||
            _options.UpperPaceCalibrationFull is < 0 or > 100 ||
            _options.UpperPaceCalibrationTaperStart is < 0 or > 100 ||
            _options.UpperPaceCalibrationEnd is < 0 or > 100 ||
            _options.UpperPaceCalibrationStart >= _options.UpperPaceCalibrationFull ||
            _options.UpperPaceCalibrationFull > _options.UpperPaceCalibrationTaperStart ||
            _options.UpperPaceCalibrationTaperStart >= _options.UpperPaceCalibrationEnd)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Upper Pace calibration options must define an ordered range within 0-100.");
        }

        if (_options.FinalStageEvidenceFactor is < 0 or > 1 ||
            _options.SemiFinalStageEvidenceFactor is < 0 or > 1 ||
            _options.QualifyingStageEvidenceFactor is < 0 or > 1 ||
            _options.SeedingStageEvidenceFactor is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Stage evidence factors must be between 0 and 1.");
        }

        if (_options.SemiFinalEarlierStageMaximumLift < 0 ||
            _options.SemiFinalHighestStagePaceCap is < 0 or > 100 ||
            _options.QualifyingHighestStagePaceCap is < 0 or > 100 ||
            _options.SeedingHighestStagePaceCap is < 0 or > 100 ||
            _options.SparseNonFinalMaximumSectorCount < 1 ||
            _options.SparseSemiFinalPaceCap is < 0 or > 100 ||
            _options.SparseQualifyingPaceCap is < 0 or > 100 ||
            _options.SparseSeedingPaceCap is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Stage confirmation limits are outside their valid ranges.");
        }

        if (_options.MaximumDominanceBonus < 0 ||
            _options.MinimumDominanceRelativeMargin < 0 ||
            _options.DominanceRelativeMarginScale <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Pace dominance bonus options are outside their valid ranges.");
        }
    }

    public IReadOnlyList<RiderSessionPaceResult> CalculateSession(PaceSessionData session)
    {
        using var measurement = ApiTelemetry.Measure("pace");
        var validRuns = session.Runs
            .Where(run => !IsDisqualified(run.Status))
            .ToArray();
        if (validRuns.Length == 0)
        {
            return [];
        }

        var sectorCount = validRuns.Max(run => run.SectorTimes.Count);
        var sectorStatistics = Enumerable.Range(0, sectorCount)
            .Select(index => BuildFieldStatistics(validRuns
                .Select(run => GetSectorTime(run, index))
                .Where(IsValidTime)
                .Select(value => value!.Value)
                .ToArray()))
            .ToArray();

        var speedStatistics = BuildSpeedStatistics(validRuns);
        var fullRunTimes = validRuns
            .Where(run => run.IsClassified)
            .Select(run => run.FinishTimeSeconds)
            .Where(IsValidTime)
            .Select(time => time!.Value)
            .ToArray();
        var fullRunStatistics = BuildFieldStatistics(fullRunTimes);
        var cumulativeStatistics = Enumerable.Range(1, sectorCount)
            .Select(cleanSectorCount => validRuns
                .Select(run => GetCumulativeTime(run, cleanSectorCount))
                .Where(IsValidTime)
                .Select(time => time!.Value)
                .ToArray())
            .Select(values => ToFieldSummary(BuildFieldStatistics(values)))
            .ToArray();
        var classifiedFieldSize = validRuns.Count(run => run.IsClassified);
        var precomputed = validRuns
            .Select(run => new PacePrecomputedSessionData(
                SessionId: string.Empty,
                CourseKey: string.Empty,
                CategoryId: null,
                SessionType: session.SessionType,
                Run: run,
                ClassifiedFieldSize: classifiedFieldSize,
                Sectors: Enumerable.Range(0, sectorCount)
                    .Select(index => new PacePrecomputedSector(
                        index,
                        GetSectorTime(run, index),
                        CalculateRank(GetSectorTime(run, index), sectorStatistics[index]),
                        ToFieldSummary(sectorStatistics[index])))
                    .ToArray(),
                SpeedStatistics: ToFieldSummary(speedStatistics),
                SpeedRank: CalculateRank(run.TopSpeed, speedStatistics, lowerIsBetter: false),
                FinishStatistics: ToFieldSummary(fullRunStatistics),
                CumulativeStatistics: cumulativeStatistics,
                CompetitionClassPriority: session.CompetitionClassPriority))
            .ToArray();
        return PrepareField(precomputed).Select(CalculatePrecomputedSession).ToArray();
    }

    /// <summary>Two passes: estimate losses, then build one corrected finish distribution.
    /// Official runs and sector observations remain untouched. Partial fields must not
    /// silently become the reference population for a rider detail or search result.</summary>
    internal IReadOnlyList<PacePrecomputedSessionData> PrepareField(IReadOnlyList<PacePrecomputedSessionData> sessions)
    {
        var prepared = new List<PacePrecomputedSessionData>(sessions.Count);
        foreach (var group in sessions.GroupBy(s => (s.SessionId, s.CategoryId)))
        {
            var field = group.ToArray();
            if (field.All(s => s.PaceFinish is not null))
            {
                prepared.AddRange(field);
                continue;
            }
            var classified = field.Where(s => s.Run.IsClassified &&
                s.Run.Status?.ToUpperInvariant() is not ("DNS" or "DNF" or "DSQ") &&
                IsValidTime(s.Run.FinishTimeSeconds)).ToArray();
            if (classified.Length == 0 || field.Any(s =>
                    (s.FinishStatistics?.Count ?? s.ClassifiedFieldSize) != classified.Length))
            {
                prepared.AddRange(field);
                continue;
            }
            var losses = classified.ToDictionary(s => s.Run.RiderId, EstimateIncidentLoss);
            var correctedTimes = classified.Select(s => s.Run.FinishTimeSeconds!.Value - losses[s.Run.RiderId]).ToArray();
            var statistics = ToFieldSummary(BuildFieldStatistics(correctedTimes));
            prepared.AddRange(field.Select(s => s with
            {
                PaceFinish = new(losses.ContainsKey(s.Run.RiderId)
                    ? s.Run.FinishTimeSeconds - losses[s.Run.RiderId] : null,
                    losses.GetValueOrDefault(s.Run.RiderId), statistics)
            }));
        }
        return prepared;
    }

    private double EstimateIncidentLoss(PacePrecomputedSessionData session)
    {
        var sectors = session.Sectors.OrderBy(s => s.SectorIndex).ToArray();
        // Never invent a missing finish, checkpoint or repair invalid timing.
        if (sectors.Length < 3 || sectors.Any(s => !IsValidTime(s.TimeSeconds)) ||
            Math.Abs(sectors.Sum(s => s.TimeSeconds!.Value) - session.Run.FinishTimeSeconds!.Value) > .01)
            return 0;
        var anomalies = SectorIncidentDetector.Analyze(sectors, _options);
        if (anomalies.Any(a => a.Kind is SectorAnomalyKind.InvalidTiming or SectorAnomalyKind.PossibleIncident ||
                (a.Kind == SectorAnomalyKind.LikelyIncident && !a.AllowsFinishCorrection)) ||
            anomalies.Count(a => a.Kind == SectorAnomalyKind.Normal) < Math.Max(2, (sectors.Length + 1) / 2))
            return 0;
        var loss = anomalies.Where(a => a.Kind == SectorAnomalyKind.LikelyIncident)
            .Sum(a => Math.Max(0, a.LossSeconds ?? 0));
        return loss < session.Run.FinishTimeSeconds ? loss : 0;
    }

    public RiderSessionPaceResult CalculatePrecomputedSession(
        PacePrecomputedSessionData session)
    {
        var run = session.Run;
        var sectorStatistics = session.Sectors
            .OrderBy(sector => sector.SectorIndex)
            .Select(sector => ToFieldStatistics(sector.Statistics))
            .ToArray();
        var raw = session.Sectors
            .OrderBy(sector => sector.SectorIndex)
            .Select((sector, index) => EvaluateRawSector(
                sector.TimeSeconds,
                sectorStatistics[index],
                sector.Rank))
            .ToArray();
        var anomalies = SectorIncidentDetector.Analyze(
            session.Sectors.OrderBy(s => s.SectorIndex).ToArray(), _options);
        int? incidentIndex = Enumerable.Range(0, raw.Length)
            .Where(i => anomalies[i].Kind == SectorAnomalyKind.LikelyIncident)
            .Select(i => (int?)i).FirstOrDefault();
        var sectorResults = new List<PaceSectorResult>(raw.Length);
        var validScores = new List<WeightedScore>(raw.Length);

        for (var index = 0; index < raw.Length; index++)
        {
            var sector = raw[index];
            if (sector.Time is null)
            {
                sectorResults.Add(new PaceSectorResult(
                    index,
                    PaceSectorStatus.Missing,
                    null,
                    null,
                    null,
                    null,
                    null));
                continue;
            }

            if (anomalies[index].Kind == SectorAnomalyKind.InvalidTiming)
            {
                sectorResults.Add(ToSectorResult(index, sector, PaceSectorStatus.InvalidTiming, null));
                continue;
            }

            if (anomalies[index].Kind == SectorAnomalyKind.LikelyIncident)
            {
                sectorResults.Add(ToSectorResult(
                    index,
                    sector,
                    index > 0 && sectorResults[^1].Status is PaceSectorStatus.Incident or PaceSectorStatus.AfterIncident
                        ? PaceSectorStatus.AfterIncident : PaceSectorStatus.Incident,
                    null));
                continue;
            }

            var status = anomalies[index].Kind == SectorAnomalyKind.PossibleIncident
                ? PaceSectorStatus.PossibleIncident : PaceSectorStatus.Valid;
            if (incidentIndex.HasValue && index > incidentIndex.Value)
            {
                if (!_options.CountPositiveSectorsAfterIncident ||
                    !(anomalies[index] is { Kind: SectorAnomalyKind.Normal, ExpectedSeconds: not null } ||
                      IsRepresentativePostIncidentSector(raw, incidentIndex.Value, sector)))
                {
                    sectorResults.Add(ToSectorResult(
                        index,
                        sector,
                        PaceSectorStatus.AfterIncident,
                        null));
                    continue;
                }

                status = PaceSectorStatus.RecoveredAfterIncident;
            }

            sectorResults.Add(ToSectorResult(index, sector, status, sector.Score));
            if (sector.Score.HasValue)
            {
                var durationWeight = Math.Sqrt(
                    sector.Statistics?.Median ?? sector.Time.Value);
                validScores.Add(new WeightedScore(sector.Score.Value, durationWeight));
            }
        }

        for (var i = 0; i < sectorResults.Count; i++)
            sectorResults[i] = sectorResults[i] with { Anomaly = anomalies[i] };

        var sectorCoverage = raw.Length > 0
            ? (double)validScores.Count / raw.Length
            : 0;
        var representative = IsRepresentativeRun(run, raw, incidentIndex);
        if (!representative ||
            validScores.Count < _options.MinimumValidSectorsForSessionPace)
        {
            return new RiderSessionPaceResult(
                run.RiderId,
                session.SessionType,
                Pace: null,
                SectorPace: null,
                SpeedScore: null,
                FullRunTimeScore: null,
                Confidence: 0,
                SessionWeight: GetConfiguredSessionWeight(session.SessionType),
                IsRepresentative: false,
                IncidentSectorIndex: incidentIndex,
                Sectors: sectorResults,
                SectorCoverage: sectorCoverage,
                NoResultReason: representative
                    ? "InsufficientSectorEvidence"
                    : "NonRepresentativeRun",
                CompetitionClassPriority: session.CompetitionClassPriority,
                SessionDate: session.SessionDate);
        }

        var sectorPace = CalculateSectorPace(validScores);
        // A reliably reconstructed isolated incident is excluded, not a missing
        // observation counted against speed twice. Keep the measured sector-count
        // requirement and original coverage/Confidence; never score the imputed sector.
        var paceSectorCount = session.PaceFinish is { RemovedLossSeconds: > 0 }
            ? raw.Length - anomalies.Count(a => a.Kind == SectorAnomalyKind.LikelyIncident)
            : raw.Length;
        var evidenceAdjustedSectorPace = AdjustSectorPaceForEvidence(
            sectorPace,
            paceSectorCount > 0 ? (double)validScores.Count / paceSectorCount : 0,
            validScores.Count);
        double? speedScore = null;
        if (IsValidSpeed(run.TopSpeed) &&
            session.SpeedStatistics is not null)
        {
            speedScore = EvaluateFieldValue(
                run.TopSpeed!.Value,
                ToFieldStatistics(session.SpeedStatistics),
                lowerIsBetter: false,
                session.SpeedRank);
        }

        var speedBoost = CalculatePositiveBoost(
            speedScore,
            evidenceAdjustedSectorPace,
            _options.TopSpeedPositiveGain);
        var fullRunTimeScore = session.PaceFinish is { } finish &&
                (!incidentIndex.HasValue || finish.RemovedLossSeconds > 0)
            ? CalculateFullRunTimeScore(finish.TimeSeconds, ToFieldStatistics(finish.Statistics))
            : CalculateEffectiveFullRunTimeScore(
            run,
            incidentIndex,
            session.FinishStatistics,
            session.CumulativeStatistics);
        var sectorAndFullRunPace = CombineSectorAndFullRunPace(
            session.SessionType,
            evidenceAdjustedSectorPace,
            fullRunTimeScore);
        var finishStatistics = session.PaceFinish?.Statistics ?? session.FinishStatistics;
        var dominanceBonus = !run.IsClassified || finishStatistics is null
            ? 0
            : CalculateDominanceBonus(
                session.PaceFinish?.TimeSeconds ?? run.FinishTimeSeconds,
                ToFieldStatistics(finishStatistics));
        var sessionPace = Clamp(
            sectorAndFullRunPace + speedBoost + dominanceBonus,
            0,
            100);

        return new RiderSessionPaceResult(
            run.RiderId,
            session.SessionType,
            Pace: sessionPace,
            SectorPace: sectorPace,
            SpeedScore: speedScore,
            FullRunTimeScore: fullRunTimeScore,
            Confidence: CalculateSessionConfidence(
                sectorResults,
                raw.Length,
                sectorStatistics,
                representative),
            SessionWeight: GetConfiguredSessionWeight(session.SessionType),
            IsRepresentative: true,
            IncidentSectorIndex: incidentIndex,
            Sectors: sectorResults,
            SectorCoverage: sectorCoverage,
            EvidenceAdjustedSectorPace: evidenceAdjustedSectorPace,
            DominanceBonus: dominanceBonus,
            CompetitionClassPriority: session.CompetitionClassPriority,
            SessionDate: session.SessionDate);
    }

    public RiderPaceResult CalculatePace(
        string riderId,
        IEnumerable<RiderSessionPaceResult> sessions,
        int? availableFinalSessions = null,
        CompetitionClassWeighting? competitionClassWeighting = null)
    {
        using var measurement = ApiTelemetry.Measure("pace");
        var validSessions = sessions
            .Where(session =>
                string.Equals(session.RiderId, riderId, StringComparison.OrdinalIgnoreCase) &&
                session.IsRepresentative &&
                session.Pace.HasValue &&
                session.SectorCoverage > 0 &&
                IsRatedSessionType(session.SessionType))
            .ToArray();

        if (validSessions.Length == 0)
        {
            return new RiderPaceResult(riderId, Pace: null, RawPace: null, Confidence: 0, UsedSessions: 0);
        }

        var qualifyingPace = CalculateStagePace(validSessions.Where(session =>
            IsQualifyingSessionType(session.SessionType)), competitionClassWeighting);
        var semiFinalPace = CalculateStagePace(validSessions.Where(session =>
            session.SessionType == PaceSessionType.SemiFinal), competitionClassWeighting);
        var finalPace = CalculateStagePace(validSessions.Where(session =>
            session.SessionType == PaceSessionType.Final), competitionClassWeighting);
        var seedingPace = CalculateStagePace(validSessions.Where(session =>
            session.SessionType == PaceSessionType.Seeding), competitionClassWeighting);

        double rawPace;
        PaceSessionType highestStage;
        if (finalPace is not null)
        {
            rawPace = CalculatePaceWithTargetFinalImpact(
                finalPace,
                [semiFinalPace, qualifyingPace, seedingPace]);
            highestStage = PaceSessionType.Final;
        }
        else if (semiFinalPace is not null)
        {
            var combinedPace = CalculateCombinedStagePace(
                semiFinalPace,
                qualifyingPace,
                seedingPace);
            rawPace = Math.Min(
                combinedPace,
                semiFinalPace.Pace + _options.SemiFinalEarlierStageMaximumLift);
            highestStage = PaceSessionType.SemiFinal;
        }
        else if (qualifyingPace is not null)
        {
            rawPace = qualifyingPace.Pace;
            highestStage = PaceSessionType.Qualifying;
        }
        else if (seedingPace is not null)
        {
            rawPace = seedingPace.Pace;
            highestStage = PaceSessionType.Seeding;
        }
        else
        {
            return new RiderPaceResult(riderId, Pace: null, RawPace: null, Confidence: 0, UsedSessions: 0);
        }

        var stageAdjustedPace = ApplyHighestStageEvidence(rawPace, highestStage);
        var calibratedPace = ApplyFinalPaceCalibration(stageAdjustedPace);
        var upperCalibratedPace = ApplyUpperPaceCalibration(calibratedPace);
        var stageCappedPace = ApplyHighestStagePaceCap(upperCalibratedPace, highestStage);
        var usedSectorCount = CountUsedSectors(validSessions);
        var pace = ApplySparseNonFinalEvidenceCap(
            stageCappedPace,
            highestStage,
            usedSectorCount);
        var totalEvidence = validSessions.Sum(session =>
            NormalizeConfidence(session.Confidence) *
            session.SessionWeight *
            (competitionClassWeighting?.GetWeight(session.CompetitionClassPriority) ?? 1.0));
        var finalSessions = validSessions
            .Where(session => session.SessionType == PaceSessionType.Final)
            .ToArray();
        var finalEvidence = finalSessions.Sum(session =>
            NormalizeConfidence(session.Confidence) *
            (competitionClassWeighting?.GetWeight(session.CompetitionClassPriority) ?? 1.0));
        var rawConfidence = 100.0 *
            (1.0 - Math.Exp(-totalEvidence / _options.OverallEvidenceScale));
        var finalConfidenceCap = CalculateFinalConfidenceCap(
            finalEvidence,
            availableFinalSessions);
        var confidence = Clamp(Math.Min(rawConfidence, finalConfidenceCap), 0, 100);
        var usedSessions = new[] { qualifyingPace, semiFinalPace, finalPace, seedingPace }
            .Where(stage => stage is not null)
            .Sum(stage => stage!.UsedSessions);

        return new RiderPaceResult(
            riderId,
            Pace: pace,
            RawPace: Clamp(stageAdjustedPace, 0, 100),
            Confidence: confidence,
            UsedSessions: usedSessions,
            UsedFinals: finalSessions.Length,
            TotalEvidence: totalEvidence,
            FinalEvidence: finalEvidence,
            ConfidenceStatus: GetConfidenceStatus(confidence));
    }

    internal double CalculateFinalConfidenceCap(
        double finalEvidence,
        int? availableFinalSessions)
    {
        finalEvidence = Math.Max(0, finalEvidence);
        if (availableFinalSessions is > 0)
        {
            if (finalEvidence == 0)
            {
                return _options.MinimumConfidenceWithoutFinals /
                    Math.Pow(
                        availableFinalSessions.Value,
                        _options.FinalParticipationConfidencePower);
            }

            var confirmedFinalCoverage = Clamp(
                finalEvidence /
                (availableFinalSessions.Value * _options.FullFinalConfidenceEvidence),
                0,
                1);
            return 100.0 * Math.Pow(
                confirmedFinalCoverage,
                _options.FinalParticipationConfidencePower);
        }

        return _options.MinimumConfidenceWithoutFinals +
            (100.0 - _options.MinimumConfidenceWithoutFinals) *
            (1.0 - Math.Exp(-finalEvidence / _options.FinalEvidenceScale));
    }

    internal StagePaceResult? CalculateStagePace(
        IEnumerable<RiderSessionPaceResult> stageSessions,
        CompetitionClassWeighting? competitionClassWeighting = null)
    {
        var weightedSessions = stageSessions
            .Where(session =>
                session.IsRepresentative &&
                session.Pace.HasValue &&
                session.SectorCoverage > 0)
            .Select(session => new
            {
                Pace = session.Pace!.Value,
                Weight = Math.Pow(
                        Clamp(session.SectorCoverage, 0, 1),
                        _options.CoverageWeightPower) *
                    (competitionClassWeighting?.GetWeight(session.CompetitionClassPriority) ?? 1.0),
                RoundWeight = session.SessionWeight
            })
            .Where(item => item.Weight > 0)
            .ToArray();

        if (weightedSessions.Length == 0)
        {
            return null;
        }

        var weightSum = weightedSessions.Sum(item => item.Weight);
        var weightedPowerSum = weightedSessions.Sum(item =>
            item.Weight * Math.Pow(item.Pace, _options.TempoPower));
        var pace = Math.Pow(weightedPowerSum / weightSum, 1.0 / _options.TempoPower);
        return new StagePaceResult(
            Clamp(pace, 0, 100),
            weightSum,
            weightedSessions.Sum(item => item.RoundWeight),
            weightedSessions.Length);
    }

    internal static double CalculateCombinedStagePace(
        params StagePaceResult?[] stages)
    {
        return WeightedMeanNormalized(stages
            .Where(stage => stage is not null && stage.RoundWeight > 0)
            .Select(stage => new WeightedPace(stage!.Pace, stage.RoundWeight))
            .ToArray());
    }

    internal double CalculatePaceWithTargetFinalImpact(
        StagePaceResult finalStage,
        IReadOnlyList<StagePaceResult?> earlierStages)
    {
        var validEarlierStages = earlierStages
            .Where(stage => stage is not null && stage.RoundWeight > 0)
            .Select(stage => stage!)
            .ToArray();
        if (validEarlierStages.Length == 0)
        {
            return finalStage.Pace;
        }

        var earlierWeightSum = validEarlierStages.Sum(stage => stage.RoundWeight);
        var components = new List<WeightedPace>
        {
            new(finalStage.Pace, _options.SessionWeights.FinalTargetImpact)
        };
        components.AddRange(validEarlierStages.Select(stage => new WeightedPace(
            stage.Pace,
            (1.0 - _options.SessionWeights.FinalTargetImpact) * stage.RoundWeight / earlierWeightSum)));
        return WeightedMeanNormalized(components);
    }

    internal double ApplyHighestStageEvidence(double pace, PaceSessionType highestStage)
    {
        pace = Clamp(pace, 0, 100);
        var factor = highestStage switch
        {
            PaceSessionType.Final => _options.FinalStageEvidenceFactor,
            PaceSessionType.SemiFinal => _options.SemiFinalStageEvidenceFactor,
            PaceSessionType.Qualifying => _options.QualifyingStageEvidenceFactor,
            PaceSessionType.Seeding => _options.SeedingStageEvidenceFactor,
            _ => 0
        };
        factor = Clamp(factor, 0, 1);

        if (factor <= 0)
        {
            return 0;
        }

        return pace <= 50
            ? pace
            : Clamp(50.0 + (pace - 50.0) * factor, 0, 100);
    }

    internal double ApplyHighestStagePaceCap(double pace, PaceSessionType highestStage)
    {
        var cap = highestStage switch
        {
            PaceSessionType.Final => 100.0,
            PaceSessionType.SemiFinal => _options.SemiFinalHighestStagePaceCap,
            PaceSessionType.Qualifying => _options.QualifyingHighestStagePaceCap,
            PaceSessionType.Seeding => _options.SeedingHighestStagePaceCap,
            _ => 0.0
        };

        return Math.Min(Clamp(pace, 0, 100), cap);
    }

    internal double ApplySparseNonFinalEvidenceCap(
        double pace,
        PaceSessionType highestStage,
        int usedSectorCount)
    {
        pace = Clamp(pace, 0, 100);
        if (highestStage == PaceSessionType.Final ||
            usedSectorCount > _options.SparseNonFinalMaximumSectorCount)
        {
            return pace;
        }

        var cap = highestStage switch
        {
            PaceSessionType.SemiFinal => _options.SparseSemiFinalPaceCap,
            PaceSessionType.Qualifying => _options.SparseQualifyingPaceCap,
            PaceSessionType.Seeding => _options.SparseSeedingPaceCap,
            _ => 0.0
        };

        return Math.Min(pace, cap);
    }

    private static int CountUsedSectors(IEnumerable<RiderSessionPaceResult> sessions)
    {
        return sessions.Sum(session => session.Sectors.Count(sector =>
            sector.Score.HasValue &&
            sector.Status is PaceSectorStatus.Valid or PaceSectorStatus.RecoveredAfterIncident));
    }

    internal double? CalculateFullRunTimeScore(
        double? riderTotalTime,
        IReadOnlyCollection<double> fieldTotalTimes)
    {
        var statistics = BuildFieldStatistics(fieldTotalTimes);
        return CalculateFullRunTimeScore(riderTotalTime, statistics);
    }

    private double? CalculateFullRunTimeScore(
        double? riderTotalTime,
        FieldStatistics statistics)
    {
        if (!IsValidTime(riderTotalTime) ||
            statistics.Count < _options.MinimumFieldSize)
        {
            return null;
        }

        var performanceZ =
            (statistics.LogMedian - Math.Log(riderTotalTime!.Value)) /
            statistics.LogMadScale;
        return Clamp(CalculateSectorTimeScore(performanceZ), 0, 100);
    }

    private double CalculateDominanceBonus(
        double? riderTotalTime,
        FieldStatistics statistics)
    {
        if (_options.MaximumDominanceBonus <= 0 ||
            !IsValidTime(riderTotalTime) ||
            statistics.Count < _options.MinimumFieldSize ||
            !IsValidTime(statistics.BestValue) ||
            !IsValidTime(statistics.SecondBestValue))
        {
            return 0;
        }

        var winningTime = statistics.BestValue!.Value;
        var secondBestTime = statistics.SecondBestValue!.Value;
        var winnerTolerance = Math.Max(0.000001, winningTime * 0.000000001);
        if (Math.Abs(riderTotalTime!.Value - winningTime) > winnerTolerance ||
            secondBestTime <= winningTime)
        {
            return 0;
        }

        var relativeMargin = (secondBestTime - winningTime) / winningTime;
        var marginAboveThreshold =
            relativeMargin - _options.MinimumDominanceRelativeMargin;
        if (marginAboveThreshold <= 0)
        {
            return 0;
        }

        var saturation = 1.0 - Math.Exp(
            -marginAboveThreshold / _options.DominanceRelativeMarginScale);
        return Clamp(
            _options.MaximumDominanceBonus * saturation,
            0,
            _options.MaximumDominanceBonus);
    }

    private double? CalculateEffectiveFullRunTimeScore(
        PaceRiderRun run,
        int? incidentIndex,
        IReadOnlyCollection<double> fieldTotalTimes,
        IReadOnlyList<IReadOnlyCollection<double>> cumulativeTimesByCheckpoint)
    {
        if (!incidentIndex.HasValue)
        {
            return run.IsClassified
                ? CalculateFullRunTimeScore(run.FinishTimeSeconds, fieldTotalTimes)
                : null;
        }

        var cleanSectorCount = incidentIndex.Value;
        if (cleanSectorCount < 2 ||
            cleanSectorCount > cumulativeTimesByCheckpoint.Count)
        {
            return null;
        }

        return CalculateFullRunTimeScore(
            GetCumulativeTime(run, cleanSectorCount),
            cumulativeTimesByCheckpoint[cleanSectorCount - 1]);
    }

    private double? CalculateEffectiveFullRunTimeScore(
        PaceRiderRun run,
        int? incidentIndex,
        PaceFieldSummary? finishStatistics,
        IReadOnlyList<PaceFieldSummary?> cumulativeStatistics)
    {
        if (!incidentIndex.HasValue)
        {
            return run.IsClassified && finishStatistics is not null
                ? CalculateFullRunTimeScore(
                    run.FinishTimeSeconds,
                    ToFieldStatistics(finishStatistics))
                : null;
        }

        var cleanSectorCount = incidentIndex.Value;
        if (cleanSectorCount < 2 ||
            cleanSectorCount > cumulativeStatistics.Count ||
            cumulativeStatistics[cleanSectorCount - 1] is not { } checkpointStatistics)
        {
            return null;
        }

        return CalculateFullRunTimeScore(
            GetCumulativeTime(run, cleanSectorCount),
            ToFieldStatistics(checkpointStatistics));
    }

    internal static double CombineSectorAndFullRunPace(
        PaceSessionType sessionType,
        double sectorPace,
        double? fullRunTimeScore)
    {
        sectorPace = Clamp(sectorPace, 0, 100);
        if (!fullRunTimeScore.HasValue)
        {
            return sectorPace;
        }

        var fullRun = Clamp(fullRunTimeScore.Value, 0, 100);
        return sessionType switch
        {
            PaceSessionType.Final => 0.85 * sectorPace + 0.15 * fullRun,
            PaceSessionType.SemiFinal => 0.90 * sectorPace + 0.10 * fullRun,
            PaceSessionType.Qualifying1 or
            PaceSessionType.Qualifying2 or
            PaceSessionType.Qualifying => 0.95 * sectorPace + 0.05 * fullRun,
            _ => sectorPace
        };
    }

    internal static double WeightedMeanNormalized(IReadOnlyCollection<WeightedPace> values)
    {
        var valid = values
            .Where(value =>
                value.Weight > 0 &&
                !double.IsNaN(value.Pace) &&
                !double.IsInfinity(value.Pace))
            .ToArray();
        if (valid.Length == 0)
        {
            return 0;
        }

        var weightSum = valid.Sum(value => value.Weight);
        return valid.Sum(value => value.Pace * value.Weight) / weightSum;
    }

    internal static double CalculatePositiveBoost(
        double? score,
        double sectorPace,
        double gain)
    {
        return score.HasValue
            ? gain * Math.Max(0, score.Value - sectorPace)
            : 0;
    }

    private RawSectorEvaluation EvaluateRawSector(
        double? riderTime,
        FieldStatistics statistics,
        int? knownRank = null)
    {
        if (!IsValidTime(riderTime) || statistics.Count < _options.MinimumFieldSize)
        {
            return new RawSectorEvaluation(riderTime, null, null, null, null, null, statistics);
        }

        var time = riderTime!.Value;
        var rank = knownRank ?? 1 + statistics.Values.Count(value => value < time);
        var percentile = CalculatePercentile(rank, statistics.Count);
        var performanceZ = (statistics.LogMedian - Math.Log(time)) / statistics.LogMadScale;
        var slowZ = -performanceZ;
        var rankScore = CalculateSectorRankScore(percentile);
        var timeScore = CalculateSectorTimeScore(performanceZ);
        var winBonus = 0.0;

        if (rank == 1 &&
            statistics.Count >= 2 &&
            IsValidTime(statistics.BestValue) &&
            IsValidTime(statistics.SecondBestValue))
        {
            var normalizedWinningMargin =
                (Math.Log(statistics.SecondBestValue!.Value) -
                    Math.Log(statistics.BestValue!.Value)) /
                statistics.LogMadScale;
            winBonus = _options.MaximumWinBonus *
                (1.0 - Math.Exp(-Math.Max(0, normalizedWinningMargin)));
        }

        var rawSectorScore = CalculateRawSectorScore(rankScore, timeScore, winBonus);

        return new RawSectorEvaluation(
            time,
            rank,
            percentile,
            performanceZ,
            slowZ,
            ApplyPositiveSectorCalibration(rawSectorScore),
            statistics);
    }

    internal double CalculateSectorRankScore(double percentile)
    {
        return 100.0 * Math.Pow(
            Clamp(percentile, 0, 1),
            _options.SectorRankExponent);
    }

    internal double CalculateSectorTimeScore(double performanceZ)
    {
        return 100.0 * Sigmoid(
            _options.SectorTimeSigmoidSlope * performanceZ -
            _options.SectorTimeSigmoidOffset);
    }

    internal double CalculateRawSectorScore(double rankScore, double timeScore, double winBonus = 0)
    {
        return _options.RankWeight * Clamp(rankScore, 0, 100) +
            _options.TimeWeight * Clamp(timeScore, 0, 100) +
            Clamp(winBonus, 0, _options.MaximumWinBonus);
    }

    internal double ApplyPositiveSectorCalibration(double rawScore)
    {
        rawScore = Clamp(rawScore, 0, 100);
        var threshold = _options.StrongSectorCalibrationThreshold;
        if (rawScore <= threshold)
        {
            return rawScore;
        }

        var normalized = (rawScore - threshold) / (100.0 - threshold);
        var calibrated = threshold +
            (100.0 - threshold) *
            Math.Pow(normalized, _options.StrongSectorCalibrationPower);
        return Clamp(calibrated, 0, 100);
    }

    internal double ApplyFinalPaceCalibration(double rawPace)
    {
        rawPace = Clamp(rawPace, 0, 100);
        var threshold = _options.PaceCalibrationThreshold;
        if (rawPace <= threshold)
        {
            return rawPace;
        }

        var normalized = (rawPace - threshold) / (100.0 - threshold);
        var calibrated = threshold +
            (100.0 - threshold) *
            Math.Pow(normalized, _options.PaceCalibrationPower);
        return Clamp(calibrated, 0, 100);
    }

    internal double ApplyUpperPaceCalibration(double pace)
    {
        pace = Clamp(pace, 0, 100);
        var entry = SmoothStep(
            _options.UpperPaceCalibrationStart,
            _options.UpperPaceCalibrationFull,
            pace);
        var ceiling = 1.0 - SmoothStep(
            _options.UpperPaceCalibrationTaperStart,
            _options.UpperPaceCalibrationEnd,
            pace);
        var boost = _options.UpperPaceCalibrationMaximumBoost * entry * ceiling;
        return Clamp(pace + boost, 0, 100);
    }

    private static double SmoothStep(double edgeStart, double edgeEnd, double value)
    {
        var normalized = Clamp((value - edgeStart) / (edgeEnd - edgeStart), 0, 1);
        return normalized * normalized * (3.0 - 2.0 * normalized);
    }

    private bool IsRepresentativePostIncidentSector(
        IReadOnlyList<RawSectorEvaluation> sectors,
        int incidentIndex,
        RawSectorEvaluation current)
    {
        if (!current.Score.HasValue ||
            !current.Percentile.HasValue ||
            !current.PerformanceZ.HasValue)
        {
            return false;
        }

        var competitiveAgainstField =
            current.Score.Value >= _options.PostIncidentMinimumPositiveScore ||
            current.PerformanceZ.Value >= _options.PostIncidentMinimumPerformanceZ;

        var baselineSectors = sectors
            .Take(incidentIndex)
            .Where(sector =>
                sector.Score.HasValue &&
                sector.Percentile.HasValue &&
                sector.PerformanceZ.HasValue)
            .TakeLast(3)
            .ToArray();
        if (baselineSectors.Length < _options.MinimumPreviousSectorsForIncident)
        {
            return competitiveAgainstField;
        }

        var baselineScore = Median(baselineSectors.Select(sector => sector.Score!.Value));
        var baselinePercentile = Median(baselineSectors.Select(sector => sector.Percentile!.Value));
        var baselinePerformance = Median(baselineSectors.Select(sector => sector.PerformanceZ!.Value));

        var representativeAgainstBaseline =
            baselineScore - current.Score.Value <= _options.PostIncidentMaximumScoreDrop &&
            baselinePercentile - current.Percentile.Value <= _options.PostIncidentMaximumPercentileDrop &&
            baselinePerformance - current.PerformanceZ.Value <= _options.PostIncidentMaximumPerformanceDrop;
        return competitiveAgainstField || representativeAgainstBaseline;
    }

    private bool IsRepresentativeRun(
        PaceRiderRun run,
        IReadOnlyList<RawSectorEvaluation> sectors,
        int? incidentIndex)
    {
        if (!run.HistoricalExpectedPercentile.HasValue)
        {
            return true;
        }

        var historical = run.HistoricalExpectedPercentile.Value;
        if (historical < _options.MinimumHistoricalPercentileForSlowRunCheck)
        {
            return true;
        }

        var openingPercentiles = sectors
            .Take(_options.SlowRunOpeningSectorCount)
            .Where(sector => sector.Percentile.HasValue)
            .Select(sector => sector.Percentile!.Value)
            .ToArray();

        if (openingPercentiles.Length < 2)
        {
            return true;
        }

        var slowFromStart = historical - Median(openingPercentiles) >= _options.SlowRunPercentileDrop;
        return !slowFromStart;
    }

    internal double CalculateSectorPace(IReadOnlyList<WeightedScore> scores)
    {
        var allSectorMean = WeightedMean(scores);
        if (scores.Count < 4)
        {
            return Clamp(allSectorMean, 0, 100);
        }

        var fastSectorCount = Math.Max(
            1,
            (int)Math.Ceiling(scores.Count * _options.FastSectorFraction));
        var fastSectorMean = WeightedMean(scores
            // SQL/JSON log statistics may differ by ~1e-12. Treat these as ties
            // when selecting sectors, retaining full precision for the weighted mean.
            .OrderByDescending(score => Math.Round(score.Score, 9))
            .Take(fastSectorCount)
            .ToArray());

        return Clamp(
            _options.FastSectorWeight * fastSectorMean +
            _options.AllSectorWeight * allSectorMean,
            0,
            100);
    }

    private double AdjustSectorPaceForEvidence(
        double sectorPace,
        double sectorCoverage,
        int validSectorCount)
    {
        if (sectorPace <= 50)
        {
            return sectorPace;
        }

        var sectorCountEvidence = Math.Min(
            1.0,
            validSectorCount / _options.FullEvidenceSectorCount);
        var evidenceFactor =
            Math.Sqrt(Clamp(sectorCoverage, 0, 1)) *
            sectorCountEvidence;

        return Clamp(
            50.0 + (sectorPace - 50.0) * evidenceFactor,
            0,
            100);
    }

    private double CalculateSessionConfidence(
        IReadOnlyList<PaceSectorResult> sectors,
        int sectorCount,
        IReadOnlyList<FieldStatistics> sectorStatistics,
        bool isRepresentative)
    {
        if (!isRepresentative || sectorCount == 0)
        {
            return 0;
        }

        // Recovered sectors may affect Pace, but neither the incident sector nor any
        // sector after it is accepted as independent evidence for Confidence.
        var validSectors = sectors
            .Where(sector =>
                sector.Status == PaceSectorStatus.Valid &&
                sector.Score.HasValue &&
                sector.SectorIndex >= 0 &&
                sector.SectorIndex < sectorStatistics.Count)
            .ToArray();
        var validSectorCount = validSectors.Length;

        if (validSectorCount < _options.MinimumValidSectorsForSessionPace)
        {
            return 0;
        }

        var medianFieldSize = Median(validSectors.Select(sector =>
            (double)sectorStatistics[sector.SectorIndex].Count));
        var sectorEvidence = 1.0 - Math.Exp(
            -validSectorCount / _options.FullConfidenceSectorCount);
        var coverageEvidence = Math.Pow(
            Clamp((double)validSectorCount / sectorCount, 0, 1),
            _options.CoverageConfidencePower);
        var fieldEvidence = Math.Sqrt(Clamp(
            medianFieldSize / _options.FullConfidenceFieldSize,
            0,
            1));
        var normalizedConfidence = Clamp(
            sectorEvidence * coverageEvidence * fieldEvidence,
            0,
            1);

        return 100.0 * normalizedConfidence;
    }

    internal static double NormalizeConfidence(double confidence)
    {
        return Clamp(confidence / 100.0, 0, 1);
    }

    internal static string GetConfidenceStatus(double confidence)
    {
        return Clamp(confidence, 0, 100) switch
        {
            < 45 => "Low",
            < 75 => "Medium",
            _ => "High"
        };
    }

    private double? EvaluateFieldValue(
        double value,
        FieldStatistics statistics,
        bool lowerIsBetter,
        int? knownRank = null)
    {
        if (statistics.Count < _options.MinimumFieldSize)
        {
            return null;
        }

        var rank = knownRank ?? (lowerIsBetter
            ? 1 + statistics.Values.Count(fieldValue => fieldValue < value)
            : 1 + statistics.Values.Count(fieldValue => fieldValue > value));
        var percentile = CalculatePercentile(rank, statistics.Count);
        var normalizedDifference = lowerIsBetter
            ? (statistics.LogMedian - Math.Log(value)) / statistics.LogMadScale
            : (value - statistics.Median) / statistics.LinearMadScale;
        var rankScore = 100 * Math.Pow(percentile, 0.8);
        var differenceScore = 100 * Sigmoid(0.9 * normalizedDifference);

        return Clamp(0.65 * rankScore + 0.35 * differenceScore, 0, 100);
    }

    private static FieldStatistics BuildSpeedStatistics(IReadOnlyList<PaceRiderRun> runs)
    {
        var speeds = runs
            .Select(run => run.TopSpeed)
            .Where(IsValidSpeed)
            .Select(speed => speed!.Value)
            .ToArray();
        return BuildFieldStatistics(speeds);
    }

    private static FieldStatistics BuildFieldStatistics(IReadOnlyCollection<double> input)
    {
        var values = input
            .Where(value => !double.IsNaN(value) && !double.IsInfinity(value) && value > 0)
            .OrderBy(value => value)
            .ToArray();

        if (values.Length == 0)
        {
            return new FieldStatistics([], 0, 0, 0, 1, 1, null, null);
        }

        var median = Median(values);
        var logs = values.Select(value => Math.Log(value)).ToArray();
        var logMedian = Median(logs);
        var logMad = Median(logs.Select(value => Math.Abs(value - logMedian)));
        var linearMad = Median(values.Select(value => Math.Abs(value - median)));
        var logMadScale = Math.Max(1.4826 * logMad, 0.003);
        var linearMadScale = Math.Max(1.4826 * linearMad, Math.Max(median * 0.003, 0.01));

        return new FieldStatistics(
            values,
            values.Length,
            median,
            logMedian,
            logMadScale,
            linearMadScale,
            values[0],
            values.Length > 1 ? values[1] : null);
    }

    private static FieldStatistics ToFieldStatistics(PaceFieldSummary summary)
    {
        return new FieldStatistics(
            [],
            summary.Count,
            summary.Median,
            summary.LogMedian,
            summary.LogMadScale,
            summary.LinearMadScale,
            summary.BestValue,
            summary.SecondBestValue);
    }

    private static PaceFieldSummary ToFieldSummary(FieldStatistics statistics)
    {
        return new PaceFieldSummary(
            statistics.Count,
            statistics.Median,
            statistics.LogMedian,
            statistics.LogMadScale,
            statistics.LinearMadScale,
            statistics.BestValue,
            statistics.SecondBestValue);
    }

    private static int? CalculateRank(
        double? value,
        FieldStatistics statistics,
        bool lowerIsBetter = true)
    {
        if (!value.HasValue || !double.IsFinite(value.Value) || value.Value <= 0)
        {
            return null;
        }

        return 1 + statistics.Values.Count(fieldValue => lowerIsBetter
            ? fieldValue < value.Value
            : fieldValue > value.Value);
    }

    private static PaceSectorResult ToSectorResult(
        int index,
        RawSectorEvaluation evaluation,
        PaceSectorStatus status,
        double? score)
    {
        return new PaceSectorResult(
            index,
            status,
            evaluation.Time,
            evaluation.Rank,
            evaluation.Percentile,
            evaluation.PerformanceZ,
            score);
    }

    private static double CalculatePercentile(int rank, int fieldSize)
    {
        return fieldSize <= 1
            ? 1
            : Clamp((double)(fieldSize - rank) / (fieldSize - 1), 0, 1);
    }

    internal static double GetSessionWeight(PaceSessionType sessionType)
    {
        return AlgorithmConfiguration.SessionWeights.GetWeight(sessionType);
    }

    internal double GetConfiguredSessionWeight(PaceSessionType sessionType) =>
        _options.SessionWeights.GetWeight(sessionType);

    internal double NonFinalistMaxScore => _options.NonFinalistMaxScore;

    internal double ApplyNonFinalistScoreCap(double score, bool isNonFinalist) =>
        isNonFinalist
            ? Math.Min(score, _options.NonFinalistMaxScore)
            : score;

    internal static bool IsQualifyingSessionType(PaceSessionType sessionType) =>
        sessionType is PaceSessionType.Qualifying1 or
            PaceSessionType.Qualifying2 or
            PaceSessionType.Qualifying;

    private static bool IsRatedSessionType(PaceSessionType sessionType) =>
        sessionType is PaceSessionType.Final or PaceSessionType.SemiFinal or PaceSessionType.Seeding ||
        IsQualifyingSessionType(sessionType);

    internal static bool IsDisqualified(string? status) =>
        string.Equals(status?.Trim(), "DSQ", StringComparison.OrdinalIgnoreCase);

    private static double WeightedMean(IReadOnlyCollection<WeightedScore> values)
    {
        var weightSum = values.Sum(value => value.Weight);
        return weightSum <= 0
            ? 0
            : values.Sum(value => value.Score * value.Weight) / weightSum;
    }

    private static double? GetSectorTime(PaceRiderRun run, int index)
    {
        return index < 0 || index >= run.SectorTimes.Count
            ? null
            : run.SectorTimes[index];
    }

    private static double? GetCumulativeTime(
        PaceRiderRun run,
        int sectorCount)
    {
        if (sectorCount <= 0 || run.SectorTimes.Count < sectorCount)
        {
            return null;
        }

        var times = run.SectorTimes.Take(sectorCount).ToArray();
        return times.All(IsValidTime)
            ? times.Sum(time => time!.Value)
            : null;
    }

    private static bool IsValidTime(double? value)
    {
        return value.HasValue &&
            value.Value > 0 &&
            !double.IsNaN(value.Value) &&
            !double.IsInfinity(value.Value);
    }

    private static bool IsValidSpeed(double? value)
    {
        return value.HasValue &&
            value.Value > 0 &&
            !double.IsNaN(value.Value) &&
            !double.IsInfinity(value.Value);
    }

    private static double Median(IEnumerable<double> input)
    {
        var values = input.OrderBy(value => value).ToArray();
        if (values.Length == 0)
        {
            return 0;
        }

        var middle = values.Length / 2;
        return values.Length % 2 == 1
            ? values[middle]
            : (values[middle - 1] + values[middle]) / 2.0;
    }

    private static double Sigmoid(double value)
    {
        if (value >= 0)
        {
            var exp = Math.Exp(-value);
            return 1.0 / (1.0 + exp);
        }

        var negativeExp = Math.Exp(value);
        return negativeExp / (1.0 + negativeExp);
    }

    private static double Clamp(double value, double minimum, double maximum)
    {
        return Math.Min(maximum, Math.Max(minimum, value));
    }

    private sealed record RawSectorEvaluation(
        double? Time,
        int? Rank,
        double? Percentile,
        double? PerformanceZ,
        double? SlowZ,
        double? Score,
        FieldStatistics? Statistics);

    private sealed record FieldStatistics(
        double[] Values,
        int Count,
        double Median,
        double LogMedian,
        double LogMadScale,
        double LinearMadScale,
        double? BestValue,
        double? SecondBestValue);

}
