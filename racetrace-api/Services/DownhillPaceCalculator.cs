using RaceTrace.Api.Infrastructure.Calculations;
using RaceTrace.Api.Infrastructure.Telemetry;

namespace RaceTrace.Api.Services;

internal sealed partial class DownhillPaceCalculator
{
    private readonly PaceOptions _options;

    public DownhillPaceCalculator(PaceOptions? options = null)
    {
        _options = options ?? AlgorithmConfiguration.Pace;

        ValidateOptions(_options);
    }

    private static void ValidateOptions(PaceOptions options)
    {
        ReadOnlySpan<double> numericOptions =
        [
            options.NonFinalistMaxScore, options.RankWeight, options.TimeWeight,
            options.MaximumWinBonus, options.SectorRankExponent, options.SectorTimeSigmoidSlope,
            options.SectorTimeSigmoidOffset, options.StrongSectorCalibrationThreshold,
            options.StrongSectorCalibrationPower, options.FastSectorFraction,
            options.FastSectorWeight, options.AllSectorWeight, options.FullEvidenceSectorCount,
            options.FullConfidenceSectorCount, options.CoverageConfidencePower,
            options.FullConfidenceFieldSize, options.OverallEvidenceScale,
            options.MinimumConfidenceWithoutFinals, options.FinalEvidenceScale,
            options.FullFinalConfidenceEvidence, options.FinalParticipationConfidencePower,
            options.CoverageWeightPower, options.FinishPositiveGain, options.TopSpeedPositiveGain,
            options.MaximumDominanceBonus, options.MinimumDominanceRelativeMargin,
            options.DominanceRelativeMarginScale, options.FinalWinBonus, options.SemiFinalWinBonus,
            options.QualifyingWinBonus, options.SeedingWinBonus, options.TimedTrainingWinBonus,
            options.TrainingWinBonus, options.IncidentPerformanceDropThreshold,
            options.IncidentRelativeLossThreshold, options.MinimumIncidentLossSeconds,
            options.PostIncidentMinimumPositiveScore, options.PostIncidentMinimumPerformanceZ,
            options.PostIncidentMaximumScoreDrop, options.PostIncidentMaximumPercentileDrop,
            options.PostIncidentMaximumPerformanceDrop, options.PaceCalibrationThreshold,
            options.PaceCalibrationPower, options.UpperPaceCalibrationMaximumBoost,
            options.UpperPaceCalibrationStart, options.UpperPaceCalibrationFull,
            options.UpperPaceCalibrationTaperStart, options.UpperPaceCalibrationEnd,
            options.FinalStageEvidenceFactor, options.SemiFinalStageEvidenceFactor,
            options.QualifyingStageEvidenceFactor, options.SeedingStageEvidenceFactor,
            options.SemiFinalEarlierStageMaximumLift, options.SemiFinalHighestStagePaceCap,
            options.QualifyingHighestStagePaceCap, options.SeedingHighestStagePaceCap,
            options.SparseSemiFinalPaceCap, options.SparseQualifyingPaceCap,
            options.SparseSeedingPaceCap, options.SlowRunPercentileDrop,
            options.MinimumHistoricalPercentileForSlowRunCheck, options.TempoPower,
            options.SessionWeights.FinalTargetImpact
        ];
        foreach (var value in numericOptions)
        {
            if (!double.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(options), "Pace options must be finite.");
        }
        if (options.TempoPower <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "TempoPower must be positive.");

        if (options.NonFinalistMaxScore is < 0 or > 100 ||
            options.SessionWeights.FinalTargetImpact is < 0 or > 1 ||
            Enum.GetValues<PaceSessionType>()
                .Select(options.SessionWeights.GetWeight)
                .Any(weight => !double.IsFinite(weight) || weight < 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Session weights must be non-negative, FinalTargetImpact must be between 0 and 1, " +
                "and NonFinalistMaxScore must be between 0 and 100.");
        }

        double maximumRawSectorScore =
            100.0 * (options.RankWeight + options.TimeWeight)
            + options.MaximumWinBonus;

        if (options.RankWeight < 0 ||
            options.RankWeight > 1 ||
            options.TimeWeight < 0 ||
            options.TimeWeight > 1 ||
            options.MaximumWinBonus < 0 ||
            maximumRawSectorScore > 100.000001)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "RankWeight and TimeWeight must be between 0 and 1, " +
                "MaximumWinBonus must be non-negative, and the maximum " +
                "raw sector score cannot exceed 100.");
        }

        if (options.StrongSectorCalibrationThreshold is < 0 or >= 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "StrongSectorCalibrationThreshold must be between 0 and 100.");
        }

        if (options.MinimumValidSectorsForSessionPace < 1 ||
            options.FullEvidenceSectorCount <= 0 ||
            options.CoverageWeightPower < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Pace evidence options must be positive.");
        }

        if (options.FullConfidenceSectorCount <= 0 ||
            options.CoverageConfidencePower < 0 ||
            options.FullConfidenceFieldSize <= 0 ||
            options.OverallEvidenceScale <= 0 ||
            options.MinimumConfidenceWithoutFinals is < 0 or > 100 ||
            options.FinalEvidenceScale <= 0 ||
            options.FullFinalConfidenceEvidence is <= 0 or > 1 ||
            options.FinalParticipationConfidencePower <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Confidence options are outside their valid ranges.");
        }

        if (options.PostIncidentMinimumPositiveScore is < 0 or > 100 ||
            !double.IsFinite(options.PostIncidentMinimumPerformanceZ) ||
            options.PostIncidentMaximumScoreDrop < 0 ||
            options.PostIncidentMaximumPercentileDrop is < 0 or > 1 ||
            options.PostIncidentMaximumPerformanceDrop < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Post-incident recovery options are outside their valid ranges.");
        }

        if (options.PaceCalibrationThreshold is < 0 or >= 100 ||
            options.PaceCalibrationPower <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Final Pace calibration options are outside their valid ranges.");
        }

        if (options.UpperPaceCalibrationMaximumBoost < 0 ||
            options.UpperPaceCalibrationStart is < 0 or > 100 ||
            options.UpperPaceCalibrationFull is < 0 or > 100 ||
            options.UpperPaceCalibrationTaperStart is < 0 or > 100 ||
            options.UpperPaceCalibrationEnd is < 0 or > 100 ||
            options.UpperPaceCalibrationStart >= options.UpperPaceCalibrationFull ||
            options.UpperPaceCalibrationFull > options.UpperPaceCalibrationTaperStart ||
            options.UpperPaceCalibrationTaperStart >= options.UpperPaceCalibrationEnd)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Upper Pace calibration options must define an ordered range within 0-100.");
        }

        if (options.FinalStageEvidenceFactor is < 0 or > 1 ||
            options.SemiFinalStageEvidenceFactor is < 0 or > 1 ||
            options.QualifyingStageEvidenceFactor is < 0 or > 1 ||
            options.SeedingStageEvidenceFactor is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Stage evidence factors must be between 0 and 1.");
        }

        if (options.SemiFinalEarlierStageMaximumLift < 0 ||
            options.SemiFinalHighestStagePaceCap is < 0 or > 100 ||
            options.QualifyingHighestStagePaceCap is < 0 or > 100 ||
            options.SeedingHighestStagePaceCap is < 0 or > 100 ||
            options.SparseNonFinalMaximumSectorCount < 1 ||
            options.SparseSemiFinalPaceCap is < 0 or > 100 ||
            options.SparseQualifyingPaceCap is < 0 or > 100 ||
            options.SparseSeedingPaceCap is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Stage confirmation limits are outside their valid ranges.");
        }

        if (options.MaximumDominanceBonus < 0 ||
            options.MinimumDominanceRelativeMargin < 0 ||
            options.DominanceRelativeMarginScale <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Pace dominance bonus options are outside their valid ranges.");
        }
    }

    public IReadOnlyList<RiderSessionPaceResult> CalculateSession(PaceSessionData session)
        => PrepareSession(session).Select(CalculatePrecomputedSession).ToArray();

    internal IReadOnlyList<PacePrecomputedSessionData> PrepareSession(PaceSessionData session,
        IReadOnlyDictionary<string, SectorAnomaly[]>? savedAnomalies = null)
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
        var cumulativeTimes = validRuns.Select(run => GetCumulativeTimes(run, sectorCount)).ToArray();
        var cumulativeStatistics = Enumerable.Range(0, sectorCount)
            .Select(index => cumulativeTimes
                .Select(times => times[index])
                .Where(IsValidTime)
                .Select(time => time!.Value)
                .ToArray())
            .Select(values => ToFieldSummary(BuildFieldStatistics(values)))
            .ToArray();
        var sectorSummaries = sectorStatistics.Select(ToFieldSummary).ToArray();
        var speedSummary = ToFieldSummary(speedStatistics);
        var finishSummary = ToFieldSummary(fullRunStatistics);
        var classifiedFieldSize = validRuns.Count(run => run.IsClassified);
        var precomputed = validRuns
            .Select((run, runIndex) => new PacePrecomputedSessionData(
                SessionId: string.Empty,
                CourseKey: string.Empty,
                CategoryId: null,
                SessionType: session.SessionType,
                Run: run,
                ClassifiedFieldSize: classifiedFieldSize,
                Sectors: Enumerable.Range(0, sectorCount)
                    .Select(index =>
                    {
                        var time = GetSectorTime(run, index);
                        return new PacePrecomputedSector(index, time,
                            CalculateRank(time, sectorStatistics[index]), sectorSummaries[index]);
                    }).ToArray(),
                SpeedStatistics: speedSummary,
                SpeedRank: CalculateRank(run.TopSpeed, speedStatistics, lowerIsBetter: false),
                FinishStatistics: finishSummary,
                CumulativeStatistics: cumulativeStatistics,
                CompetitionClassPriority: session.CompetitionClassPriority)
            {
                PreparedCumulativeTimes = (run, cumulativeTimes[runIndex])
            })
            .ToArray();
        if (savedAnomalies is not null && ReferenceEquals(_options, AlgorithmConfiguration.Pace))
            for (var i = 0; i < precomputed.Length; i++)
            {
                var row = precomputed[i];
                if (savedAnomalies.TryGetValue(row.Run.RiderId, out var annotations) && annotations.Length == row.Sectors.Count &&
                    IncidentAnnotationCache.Materialize(annotations, row.Sectors, _options) is { } materialized)
                    precomputed[i] = row with { PreparedAnomalies = (row.Sectors, _options, materialized) };
            }
        return PrepareField(precomputed);
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
            var losses = new Dictionary<string, (double Loss, IReadOnlyList<SectorAnomaly>? Anomalies)>(classified.Length);
            var correctedTimes = new double[classified.Length];
            for (var index = 0; index < classified.Length; index++)
            {
                var session = classified[index];
                var loss = EstimateIncidentLoss(session, out var anomalies);
                losses.Add(session.Run.RiderId, (loss, anomalies));
                correctedTimes[index] = session.Run.FinishTimeSeconds!.Value - loss;
            }
            var statistics = ToFieldSummary(BuildFieldStatistics(correctedTimes));
            foreach (var session in field)
            {
                var hasLoss = losses.TryGetValue(session.Run.RiderId, out var entry);
                prepared.Add(session with
                {
                    PaceFinish = new(hasLoss ? session.Run.FinishTimeSeconds - entry.Loss : null,
                        hasLoss ? entry.Loss : 0, statistics),
                    PreparedAnomalies = hasLoss && entry.Anomalies is not null
                        ? (session.Sectors, _options, entry.Anomalies) : session.PreparedAnomalies
                });
            }
        }
        return prepared;
    }

    private double EstimateIncidentLoss(
        PacePrecomputedSessionData session,
        out IReadOnlyList<SectorAnomaly>? anomalies)
    {
        anomalies = null;
        var sectors = session.Sectors.OrderBy(s => s.SectorIndex).ToArray();
        // Never invent a missing finish, checkpoint or repair invalid timing.
        if (sectors.Length < 3 || sectors.Any(s => !IsValidTime(s.TimeSeconds)) ||
            Math.Abs(sectors.Sum(s => s.TimeSeconds!.Value) - session.Run.FinishTimeSeconds!.Value) > .01)
            return 0;
        anomalies = session.PreparedAnomalies is { } saved &&
            ReferenceEquals(saved.Sectors, session.Sectors) && ReferenceEquals(saved.Options, _options)
                ? saved.Values : SectorIncidentDetector.Analyze(sectors, _options);
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
        var orderedSectors = session.Sectors.OrderBy(sector => sector.SectorIndex).ToArray();
        var sectorStatistics = orderedSectors
            .Select(sector => ToFieldStatistics(sector.Statistics))
            .ToArray();
        var raw = orderedSectors
            .Select((sector, index) => EvaluateRawSector(
                sector.TimeSeconds,
                sectorStatistics[index],
                sector.Rank))
            .ToArray();
        var anomalies = session.PreparedAnomalies is { } prepared &&
            ReferenceEquals(prepared.Sectors, session.Sectors) &&
            ReferenceEquals(prepared.Options, _options)
            ? prepared.Values : SectorIncidentDetector.Analyze(orderedSectors, _options);
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
                    null) { Anomaly = anomalies[index] });
                continue;
            }

            if (anomalies[index].Kind == SectorAnomalyKind.InvalidTiming)
            {
                sectorResults.Add(ToSectorResult(index, sector, PaceSectorStatus.InvalidTiming, null, anomalies[index]));
                continue;
            }

            if (anomalies[index].Kind == SectorAnomalyKind.LikelyIncident)
            {
                sectorResults.Add(ToSectorResult(
                    index,
                    sector,
                    index > 0 && sectorResults[^1].Status is PaceSectorStatus.Incident or PaceSectorStatus.AfterIncident
                        ? PaceSectorStatus.AfterIncident : PaceSectorStatus.Incident,
                    null, anomalies[index]));
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
                        null, anomalies[index]));
                    continue;
                }

                status = PaceSectorStatus.RecoveredAfterIncident;
            }

            sectorResults.Add(ToSectorResult(index, sector, status, sector.Score, anomalies[index]));
            if (sector.Score.HasValue)
            {
                var durationWeight = Math.Sqrt(
                    sector.Statistics?.Median ?? sector.Time.Value);
                validScores.Add(new WeightedScore(sector.Score.Value, durationWeight));
            }
        }

        var sectorCoverage = raw.Length > 0
            ? (double)validScores.Count / raw.Length
            : 0;
        var representative = IsRepresentativeRun(run, raw);
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
        if (IsValidTime(run.TopSpeed) &&
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
            : CalculateEffectiveFullRunTimeScore(session, incidentIndex);
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
        var validSessions = new List<RiderSessionPaceResult>();
        var qualifyingSessions = new List<(RiderSessionPaceResult Session, double ClassWeight)>();
        var semiFinalSessions = new List<(RiderSessionPaceResult Session, double ClassWeight)>();
        var finalSessions = new List<(RiderSessionPaceResult Session, double ClassWeight)>();
        var seedingSessions = new List<(RiderSessionPaceResult Session, double ClassWeight)>();
        double totalEvidence = 0, finalEvidence = 0;
        foreach (var session in sessions)
        {
            if (!string.Equals(session.RiderId, riderId, StringComparison.OrdinalIgnoreCase) ||
                !session.IsRepresentative || !session.Pace.HasValue ||
                !(session.SectorCoverage > 0) || !IsRatedSessionType(session.SessionType)) continue;
            validSessions.Add(session);
            var classWeight = competitionClassWeighting?.GetWeight(session.CompetitionClassPriority) ?? 1.0;
            var weightedSession = (session, classWeight);
            if (IsQualifyingSessionType(session.SessionType)) qualifyingSessions.Add(weightedSession);
            else if (session.SessionType == PaceSessionType.SemiFinal) semiFinalSessions.Add(weightedSession);
            else if (session.SessionType == PaceSessionType.Final) finalSessions.Add(weightedSession);
            else seedingSessions.Add(weightedSession);
            var normalizedConfidence = NormalizeConfidence(session.Confidence);
            totalEvidence += normalizedConfidence * session.SessionWeight * classWeight;
            if (session.SessionType == PaceSessionType.Final)
                finalEvidence += normalizedConfidence * classWeight;
        }

        if (validSessions.Count == 0)
        {
            return new RiderPaceResult(riderId, Pace: null, RawPace: null, Confidence: 0, UsedSessions: 0);
        }

        var qualifyingPace = CalculateStagePace(qualifyingSessions);
        var semiFinalPace = CalculateStagePace(semiFinalSessions);
        var finalPace = CalculateStagePace(finalSessions);
        var seedingPace = CalculateStagePace(seedingSessions);

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
        var rawConfidence = 100.0 *
            (1.0 - Math.Exp(-totalEvidence / _options.OverallEvidenceScale));
        var finalConfidenceCap = CalculateFinalConfidenceCap(
            finalEvidence,
            availableFinalSessions);
        var confidence = Clamp(Math.Min(rawConfidence, finalConfidenceCap), 0, 100);
        var usedSessions = (qualifyingPace?.UsedSessions ?? 0) +
            (semiFinalPace?.UsedSessions ?? 0) + (finalPace?.UsedSessions ?? 0) +
            (seedingPace?.UsedSessions ?? 0);

        return new RiderPaceResult(
            riderId,
            Pace: pace,
            RawPace: Clamp(stageAdjustedPace, 0, 100),
            Confidence: confidence,
            UsedSessions: usedSessions,
            UsedFinals: finalSessions.Count,
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
        => CalculateStagePace(stageSessions.Select(session =>
            (session, competitionClassWeighting?.GetWeight(session.CompetitionClassPriority) ?? 1.0)));

    private StagePaceResult? CalculateStagePace(
        IEnumerable<(RiderSessionPaceResult Session, double ClassWeight)> stageSessions)
    {
        double weightSum = 0, weightedPowerSum = 0, roundWeightSum = 0;
        var usedSessions = 0;
        foreach (var (session, classWeight) in stageSessions)
        {
            if (!session.IsRepresentative || !session.Pace.HasValue || !(session.SectorCoverage > 0)) continue;
            var weight = Math.Pow(Clamp(session.SectorCoverage, 0, 1), _options.CoverageWeightPower) *
                classWeight;
            if (weight <= 0 || double.IsNaN(weight)) continue;
            weightSum += weight;
            weightedPowerSum += weight * Math.Pow(session.Pace.Value, _options.TempoPower);
            roundWeightSum += session.SessionWeight;
            usedSessions++;
        }

        if (usedSessions == 0)
        {
            return null;
        }

        var pace = Math.Pow(weightedPowerSum / weightSum, 1.0 / _options.TempoPower);
        return new StagePaceResult(
            Clamp(pace, 0, 100),
            weightSum,
            roundWeightSum,
            usedSessions);
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
        PacePrecomputedSessionData session,
        int? incidentIndex)
    {
        var run = session.Run;
        if (!incidentIndex.HasValue)
        {
            return run.IsClassified && session.FinishStatistics is not null
                ? CalculateFullRunTimeScore(
                    run.FinishTimeSeconds,
                    ToFieldStatistics(session.FinishStatistics))
                : null;
        }

        var cleanSectorCount = incidentIndex.Value;
        if (cleanSectorCount < 2 ||
            cleanSectorCount > session.CumulativeStatistics.Count ||
            session.CumulativeStatistics[cleanSectorCount - 1] is not { } checkpointStatistics)
        {
            return null;
        }

        var prefix = session.PreparedCumulativeTimes;
        var time = prefix is { } prepared && ReferenceEquals(prepared.Run, run) &&
            cleanSectorCount <= prepared.Times.Count
            ? prepared.Times[cleanSectorCount - 1]
            : GetCumulativeTime(run, cleanSectorCount);
        return CalculateFullRunTimeScore(
            time,
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
            return new RawSectorEvaluation(riderTime, null, null, null, null, statistics);
        }

        var time = riderTime!.Value;
        var rank = knownRank ?? CalculateRank(time, statistics)!.Value;
        var percentile = CalculatePercentile(rank, statistics.Count);
        var performanceZ = (statistics.LogMedian - Math.Log(time)) / statistics.LogMadScale;
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
        IReadOnlyList<RawSectorEvaluation> sectors)
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
            (double)validSectorCount / _options.FullEvidenceSectorCount);
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
            -(double)validSectorCount / _options.FullConfidenceSectorCount);
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

        var rank = knownRank ?? CalculateRank(value, statistics, lowerIsBetter)!.Value;
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
            .Where(IsValidTime)
            .Select(speed => speed!.Value)
            .ToArray();
        return BuildFieldStatistics(speeds);
    }

    private static PaceSectorResult ToSectorResult(
        int index,
        RawSectorEvaluation evaluation,
        PaceSectorStatus status,
        double? score,
        SectorAnomaly anomaly)
    {
        return new PaceSectorResult(
            index,
            status,
            evaluation.Time,
            evaluation.Rank,
            evaluation.Percentile,
            evaluation.PerformanceZ,
            score) { Anomaly = anomaly };
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

    private static double?[] GetCumulativeTimes(PaceRiderRun run, int sectorCount)
    {
        var times = new double?[sectorCount];
        double total = 0;
        for (var index = 0; index < run.SectorTimes.Count; index++)
        {
            var time = run.SectorTimes[index];
            if (!IsValidTime(time)) break;
            total += time!.Value;
            times[index] = total;
        }
        return times;
    }

    private static double? GetCumulativeTime(
        PaceRiderRun run,
        int sectorCount)
    {
        if (sectorCount <= 0 || run.SectorTimes.Count < sectorCount)
        {
            return null;
        }

        double total = 0;
        for (var index = 0; index < sectorCount; index++)
        {
            var time = run.SectorTimes[index];
            if (!IsValidTime(time)) return null;
            total += time!.Value;
        }
        return total;
    }

    private static bool IsValidTime(double? value)
    {
        return value.HasValue &&
            value.Value > 0 &&
            !double.IsNaN(value.Value) &&
            !double.IsInfinity(value.Value);
    }

    private sealed record RawSectorEvaluation(
        double? Time,
        int? Rank,
        double? Percentile,
        double? PerformanceZ,
        double? Score,
        FieldStatistics? Statistics);

}
