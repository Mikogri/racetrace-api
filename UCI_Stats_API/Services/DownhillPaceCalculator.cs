namespace UCI_Stats_API.Services;

internal enum PaceSessionType
{
    Final,
    SemiFinal,
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
    int? CompetitionClassPriority = null);

internal enum PaceSectorStatus
{
    Valid,
    Incident,
    RecoveredAfterIncident,
    AfterIncident,
    Missing
}

internal sealed record PaceSectorResult(
    int SectorIndex,
    PaceSectorStatus Status,
    double? Time,
    int? Rank,
    double? Percentile,
    double? PerformanceZ,
    double? Score);

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
    int? CompetitionClassPriority = null);

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

internal sealed record StagePaceResult(double Pace, double EvidenceWeight, int UsedSessions);

internal sealed class PaceOptions
{
    // Udział pozycji/percentyla w surowym wyniku sektora. Wyższa wartość mocniej
    // premiuje wysokie miejsca; niższa zwiększa znaczenie samej straty czasowej.
    public double RankWeight { get; init; } = 0.20; // 0.25

    // Udział marginesu czasowego względem stawki w wyniku sektora. Wyższa wartość
    // mocniej reaguje na przewagę lub stratę czasową; zwykle równoważy RankWeight.
    public double TimeWeight { get; init; } = 0.75; // 0.6

    // Maksymalny bonus za wygranie sektora dużym marginesem. Wyższa wartość mocniej
    // odróżnia dominujące wygrane sektorów; 0 całkowicie wyłącza ten bonus.
    public double MaximumWinBonus { get; init; } = 5; // 5.0

    // Wykładnik krzywej wyniku pozycji sektorowej. Wyższa wartość mocniej obniża
    // wyniki środka i końca stawki; niższa spłaszcza różnice między pozycjami.
    public double SectorRankExponent { get; init; } = 1.60;

    // Stromość sigmoidy zamieniającej PerformanceZ na wynik czasu. Wyższa wartość
    // szybciej rozsuwa dobre i słabe czasy, a niższa daje łagodniejsze różnice.
    public double SectorTimeSigmoidSlope { get; init; } = 1.25;

    // Przesunięcie sigmoidy czasu. Wyższa wartość obniża wynik dla tego samego czasu,
    // szczególnie w pobliżu mediany; niższa podnosi środkową część skali.
    public double SectorTimeSigmoidOffset { get; init; } = -0.05; // 0.00

    // Próg rozpoczęcia pozytywnej kalibracji mocnych sektorów. Wyższy próg ogranicza
    // premię do ścisłej czołówki; niższy obejmuje nią więcej przeciętnych wyników.
    public double StrongSectorCalibrationThreshold { get; init; } = 75.0;

    // Siła kalibracji powyżej progu. W zakresie 0-1 niższa wartość daje większe
    // podbicie, wartość 1 wyłącza podbicie, a wartość powyżej 1 obniża wynik.
    public double StrongSectorCalibrationPower { get; init; } = 0.60; // 0.65

    // Część najlepszych sektorów używana do FastSectorMean. Wyższa wartość obejmuje
    // więcej sektorów i łagodzi premię za szczytowe tempo; niższa skupia się na najlepszych.
    public double FastSectorFraction { get; init; } = 0.50; // 0.6

    // Waga średniej z najlepszych sektorów, używana przy co najmniej 4 sektorach.
    // Wyższa mocniej premiuje potencjał i pojedyncze mocne sektory.
    public double FastSectorWeight { get; init; } = 0.70;

    // Waga średniej ze wszystkich poprawnych sektorów. Wyższa sprawia, że słabsze
    // sektory mocniej obniżają Pace; z FastSectorWeight zwykle powinna sumować się do 1.
    public double AllSectorWeight { get; init; } = 0.30;

    // Minimalna liczba ocenionych sektorów potrzebna do Pace sesji. Wyższa wartość
    // odrzuca więcej częściowych przejazdów; niższa dopuszcza wyniki z mniejszej próbki.
    public int MinimumValidSectorsForSessionPace { get; init; } = 2;

    // Liczba poprawnych sektorów dająca pełny współczynnik ilości dowodów. Wyższa
    // mocniej ogranicza Pace krótkich przejazdów; niższa szybciej uznaje dane za pełne.
    public double FullEvidenceSectorCount { get; init; } = 4.0;

    // Number of valid sectors at which session confidence reaches 1 - exp(-1).
    public double FullConfidenceSectorCount { get; init; } = 4.0;

    // Controls how strongly incomplete route coverage lowers session confidence.
    public double CoverageConfidencePower { get; init; } = 0.5;

    // Median sector field size that provides full field-size confidence.
    public double FullConfidenceFieldSize { get; init; } = 30.0;

    // Evidence needed for overall confidence to reach 1 - exp(-1).
    public double OverallEvidenceScale { get; init; } = 4.0;

    // Maximum confidence available without evidence from a final.
    public double MinimumConfidenceWithoutFinals { get; init; } = 40.0;

    // Final-session evidence needed to progressively remove the no-final cap.
    public double FinalEvidenceScale { get; init; } = 3.0;

    // Normalized confidence expected from one sufficiently complete final.
    public double FullFinalConfidenceEvidence { get; init; } = 0.50;

    // Curvature of the final-participation cap. Higher values penalize missing finals more.
    public double FinalParticipationConfidencePower { get; init; } = 0.75;

    // Wykładnik pokrycia trasy w wadze sesji. Wyższy silniej osłabia sesje częściowe,
    // wartość 1 działa liniowo, a 0 całkowicie ignoruje pokrycie przy łączeniu sesji.
    public double CoverageWeightPower { get; init; } = 2.0;

    // Opcja zachowana dla zgodności konfiguracji. Dawny FinishScore pozostaje wyłączony;
    // wpływ czasu mety ogranicza się do osobnego bonusu za dominującą przewagę nad P2.
    public double FinishPositiveGain { get; init; } = 0.0;

	// Część dodatniej różnicy SpeedScore względem Pace sektorów dodawana do sesji.
	// Wyższa zwiększa wpływ top speed; 0 sprawia, że prędkość nie zmienia Pace.
	public double TopSpeedPositiveGain { get; init; } = 0.02;

    // Maksymalny bonus Pace dla zwycięzcy sesji za wyraźną przewagę czasową nad P2.
    public double MaximumDominanceBonus { get; init; } = 3.0;

    // Minimalna przewaga względem czasu zwycięzcy, od której zaczyna działać bonus.
    // Wartość 0.0025 oznacza 0.25%, czyli około 0.6 s na czterominutowej trasie.
    public double MinimumDominanceRelativeMargin { get; init; } = 0.0025;

    // Skala nasycenia bonusu. Niższa szybciej zbliża dominujące wygrane do maksimum.
    public double DominanceRelativeMarginScale { get; init; } = 0.006;

    // Dawne stałe bonusy za samo P1 są zachowane dla zgodności, ale pozostają bez wpływu.
    // Zwycięstwo zwiększa Pace wyłącznie przez wyliczony wyżej bonus przewagi czasowej.
    public double FinalWinBonus { get; init; } = 0.0;
    public double SemiFinalWinBonus { get; init; } = 0.0;
    public double QualifyingWinBonus { get; init; } = 0.0;
    public double SeedingWinBonus { get; init; } = 0.0;
    public double TimedTrainingWinBonus { get; init; } = 0.0;
    public double TrainingWinBonus { get; init; } = 0.0;

    // Minimalna liczba wcześniejszych sektorów potrzebna do wykrycia incydentu.
    // Wyższa opóźnia detekcję i ogranicza fałszywe alarmy; niższa reaguje wcześniej.
    public int MinimumPreviousSectorsForIncident { get; init; } = 1;

    // Minimalna skrajność SlowZ podejrzanego sektora. Wyższa wymaga bardziej
    // ekstremalnie wolnego czasu; niższa zwiększa czułość wykrywania incydentów.
    public double IncidentSlowZThreshold { get; init; } = 4.5;

    // Minimalny nagły spadek PerformanceZ względem wcześniejszych sektorów.
    // Wyższa wartość wykrywa tylko gwałtowniejsze załamania tempa.
    public double IncidentPerformanceDropThreshold { get; init; } = 4.0;

    // Minimalny spadek percentyla będący dodatkowym sygnałem incydentu. Wyższa
    // wartość wymaga większej utraty pozycji w stawce; niższa jest bardziej czuła.
    public double IncidentPercentileDropThreshold { get; init; } = 0.50;

    // Minimalna strata względem mediany jako część czasu sektora, np. 0.10 to 10%.
    // Wyższa ignoruje mniejsze względne straty; niższa częściej potwierdza incydent.
    public double IncidentRelativeLossThreshold { get; init; } = 0.10;

    // Minimalna bezwzględna strata do mediany sektora w sekundach. Wyższa wartość
    // pomija krótsze błędy, a niższa pozwala wykrywać mniej kosztowne incydenty.
    public double MinimumIncidentLossSeconds { get; init; } = 3.0;

    // Określa, czy pozytywne i miarodajne sektory po incydencie mogą być liczone.
    // false odrzuca wszystkie sektory po incydencie, true stosuje poniższe progi powrotu.
    public bool CountPositiveSectorsAfterIncident { get; init; } = true;

    // Minimalny wynik sektora po incydencie uznawany za pozytywny. Wyższa wartość
    // przywraca tylko mocniejsze sektory; niższa dopuszcza tempo bliższe środkowi stawki.
    public double PostIncidentMinimumPositiveScore { get; init; } = 50.0;

    // Maksymalny dopuszczalny spadek wyniku sektora względem tempa sprzed incydentu.
    // Wyższa wartość szerzej uznaje tempo po incydencie za miarodajne dla ridera.
    public double PostIncidentMaximumScoreDrop { get; init; } = 15.0;

    // Maksymalny spadek percentyla względem tempa sprzed incydentu. Wartość 0.20
    // pozwala stracić najwyżej 20 punktów percentylowych przy zachowaniu sektora.
    public double PostIncidentMaximumPercentileDrop { get; init; } = 0.20;

    // Maksymalny spadek PerformanceZ względem tempa sprzed incydentu. Wyższa wartość
    // łagodniej ocenia pogorszenie czasu, a niższa wymaga szybszego powrotu do tempa.
    public double PostIncidentMaximumPerformanceDrop { get; init; } = 1.5;

    // Próg końcowej kalibracji Pace po połączeniu sesji. Wyniki do progu pozostają
    // bez zmian, a wyższe wartości otrzymują kontrolowane podbicie ścisłej czołówki.
    public double PaceCalibrationThreshold { get; init; } = 55.0; // 72.0

    // Potęga końcowej kalibracji Pace. Wartość poniżej 1 podnosi wyniki ponad progiem;
    // im niższa wartość, tym mocniejsze podbicie przy zachowaniu kolejności riderów.
    public double PaceCalibrationPower { get; init; } = 0.50; // 0.72

    // Maksymalne dodatkowe podbicie górnego zakresu Pace. Krzywa płynnie wchodzi
    // między Start i Full, utrzymuje pełny wpływ, a następnie wygasa do 100.
    public double UpperPaceCalibrationMaximumBoost { get; init; } = 1.5;
    public double UpperPaceCalibrationStart { get; init; } = 60.0;
    public double UpperPaceCalibrationFull { get; init; } = 70.0;
    public double UpperPaceCalibrationTaperStart { get; init; } = 95.0;
    public double UpperPaceCalibrationEnd { get; init; } = 100.0;

    // Controls access to the upper half of the Pace scale based on the highest stage reached.
    public double FinalStageEvidenceFactor { get; init; } = 1.00;
    public double SemiFinalStageEvidenceFactor { get; init; } = 0.96;
    public double QualifyingStageEvidenceFactor { get; init; } = 0.90;
    public double SeedingStageEvidenceFactor { get; init; } = 0.80;

    // A fast earlier stage may improve a semi-final result only by this many raw Pace points.
    public double SemiFinalEarlierStageMaximumLift { get; init; } = 3.0;

    // Final caps applied after global calibration when the rider did not reach a later stage.
    public double SemiFinalHighestStagePaceCap { get; init; } = 90.0;
    public double QualifyingHighestStagePaceCap { get; init; } = 82.0;
    public double SeedingHighestStagePaceCap { get; init; } = 75.0;

    // Maksymalna łączna liczba użytecznych sektorów uznawana za bardzo małą próbkę.
    // Kara działa tylko wtedy, gdy rider nie dotarł do finału.
    public int SparseNonFinalMaximumSectorCount { get; init; } = 4;

    // Limity Pace dla bardzo małej próbki bez finału. Wyższa wartość łagodzi karę,
    // a niższa mocniej chroni ranking przed wysoką oceną z kilku dobrych sektorów.
    public double SparseSemiFinalPaceCap { get; init; } = 78.0;
    public double SparseQualifyingPaceCap { get; init; } = 72.0;
    public double SparseSeedingPaceCap { get; init; } = 68.0;

	// Liczba początkowych sektorów używana do oceny wolnego przejazdu od startu.
	// Wyższa daje szerszy obraz otwarcia; niższa szybciej podejmuje decyzję.
	public int SlowRunOpeningSectorCount { get; init; } = 3;

    // Wymagany spadek mediany percentyla otwarcia względem historycznego poziomu.
    // Wyższa odrzuca tylko wyraźnie słabsze przejazdy; niższa jest bardziej restrykcyjna.
    public double SlowRunPercentileDrop { get; init; } = 0.30;

    // Minimalny historyczny percentyl ridera, od którego działa kontrola slow run.
    // Wyższa obejmuje tylko mocniejszych riderów; niższa stosuje kontrolę szerzej.
    public double MinimumHistoricalPercentileForSlowRunCheck { get; init; } = 0.65;

    // Minimalna liczba poprawnych czasów stawki potrzebna do oceny sektora.
    // Wyższa poprawia wiarygodność, ale częściej daje brak oceny i mniejsze pokrycie.
    public int MinimumFieldSize { get; init; } = 3;

    // Potęga średniej łączącej Pace z wielu sesji. Wyższa mocniej podkreśla najlepsze
    // sesje ridera; wartość 1 oznacza zwykłą średnią ważoną.
    public double TempoPower { get; init; } = 5.0;
}

internal sealed class DownhillPaceCalculator
{
    private readonly PaceOptions _options;

    public DownhillPaceCalculator(PaceOptions? options = null)
    {
		_options = options ?? new PaceOptions();

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
        if (session.Runs.Count == 0)
        {
            return [];
        }

        var sectorCount = session.Runs.Max(run => run.SectorTimes.Count);
        var sectorStatistics = Enumerable.Range(0, sectorCount)
            .Select(index => BuildFieldStatistics(session.Runs
                .Select(run => GetSectorTime(run, index))
                .Where(IsValidTime)
                .Select(value => value!.Value)
                .ToArray()))
            .ToArray();

        var rawSectorData = new Dictionary<string, RawSectorEvaluation[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var run in session.Runs)
        {
            var evaluations = new RawSectorEvaluation[sectorCount];
            for (var sectorIndex = 0; sectorIndex < sectorCount; sectorIndex++)
            {
                evaluations[sectorIndex] = EvaluateRawSector(
                    GetSectorTime(run, sectorIndex),
                    sectorStatistics[sectorIndex]);
            }

            rawSectorData[run.RiderId] = evaluations;
        }

        var speedStatistics = BuildSpeedStatistics(session.Runs);
        var fullRunTimes = session.Runs
            .Where(run => run.IsClassified)
            .Select(run => run.FinishTimeSeconds)
            .Where(IsValidTime)
            .Select(time => time!.Value)
            .ToArray();
        var fullRunStatistics = BuildFieldStatistics(fullRunTimes);
        var cumulativeTimesByCheckpoint = Enumerable.Range(1, sectorCount)
            .Select(cleanSectorCount => session.Runs
                .Select(run => GetCumulativeTime(run, cleanSectorCount))
                .Where(IsValidTime)
                .Select(time => time!.Value)
                .ToArray())
            .ToArray();
        var results = new List<RiderSessionPaceResult>();

        foreach (var run in session.Runs)
        {
            var raw = rawSectorData[run.RiderId];
            var incidentIndex = DetectIncident(raw);
            var sectorResults = new List<PaceSectorResult>();
            var validScores = new List<WeightedScore>();

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

                if (incidentIndex == index)
                {
                    sectorResults.Add(ToSectorResult(index, sector, PaceSectorStatus.Incident, null));
                    continue;
                }

                var status = PaceSectorStatus.Valid;
                if (incidentIndex.HasValue && index > incidentIndex.Value)
                {
                    if (!_options.CountPositiveSectorsAfterIncident ||
                        !IsRepresentativePostIncidentSector(raw, incidentIndex.Value, sector))
                    {
                        sectorResults.Add(ToSectorResult(index, sector, PaceSectorStatus.AfterIncident, null));
                        continue;
                    }

                    status = PaceSectorStatus.RecoveredAfterIncident;
                }

                sectorResults.Add(ToSectorResult(index, sector, status, sector.Score));
                if (sector.Score.HasValue)
                {
                    var durationWeight = Math.Sqrt(sector.Statistics?.Median ?? sector.Time.Value);
                    validScores.Add(new WeightedScore(sector.Score.Value, durationWeight));
                }
            }

            var sectorCoverage = sectorCount > 0
                ? (double)validScores.Count / sectorCount
                : 0;
            var representative = IsRepresentativeRun(run, raw, incidentIndex);
            if (!representative ||
                validScores.Count < _options.MinimumValidSectorsForSessionPace)
            {
                results.Add(new RiderSessionPaceResult(
                    run.RiderId,
                    session.SessionType,
                    Pace: null,
                    SectorPace: null,
                    SpeedScore: null,
                    FullRunTimeScore: null,
                    Confidence: 0,
                    SessionWeight: GetSessionWeight(session.SessionType),
                    IsRepresentative: false,
                    IncidentSectorIndex: incidentIndex,
                    Sectors: sectorResults,
                    SectorCoverage: sectorCoverage,
                    NoResultReason: representative
                        ? "InsufficientSectorEvidence"
                        : "NonRepresentativeRun",
                    CompetitionClassPriority: session.CompetitionClassPriority));
                continue;
            }

            var sectorPace = CalculateSectorPace(validScores);
            var evidenceAdjustedSectorPace = AdjustSectorPaceForEvidence(
                sectorPace,
                sectorCoverage,
                validScores.Count);
            double? speedScore = null;
            if (IsValidSpeed(run.TopSpeed))
            {
                speedScore = EvaluateFieldValue(run.TopSpeed!.Value, speedStatistics, lowerIsBetter: false);
            }

            var speedBoost = CalculatePositiveBoost(
                speedScore,
                evidenceAdjustedSectorPace,
                _options.TopSpeedPositiveGain);
            var fullRunTimeScore = CalculateEffectiveFullRunTimeScore(
                run,
                incidentIndex,
                fullRunTimes,
                cumulativeTimesByCheckpoint);
            var sectorAndFullRunPace = CombineSectorAndFullRunPace(
                session.SessionType,
                evidenceAdjustedSectorPace,
                fullRunTimeScore);
            var dominanceBonus = run.IsClassified
                ? CalculateDominanceBonus(
                    run.FinishTimeSeconds,
                    fullRunStatistics)
                : 0;
            var sessionPace = Clamp(
                sectorAndFullRunPace + speedBoost + dominanceBonus,
                0,
                100);

            results.Add(new RiderSessionPaceResult(
                run.RiderId,
                session.SessionType,
                Pace: sessionPace,
                SectorPace: sectorPace,
                SpeedScore: speedScore,
                FullRunTimeScore: fullRunTimeScore,
                Confidence: CalculateSessionConfidence(
                    sectorResults,
                    sectorCount,
                    sectorStatistics,
                    representative),
                SessionWeight: GetSessionWeight(session.SessionType),
                IsRepresentative: true,
                IncidentSectorIndex: incidentIndex,
                Sectors: sectorResults,
                SectorCoverage: sectorCoverage,
                EvidenceAdjustedSectorPace: evidenceAdjustedSectorPace,
                DominanceBonus: dominanceBonus,
                CompetitionClassPriority: session.CompetitionClassPriority));
        }

        return results;
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
        var incidentIndex = DetectIncident(raw);
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

            if (incidentIndex == index)
            {
                sectorResults.Add(ToSectorResult(
                    index,
                    sector,
                    PaceSectorStatus.Incident,
                    null));
                continue;
            }

            var status = PaceSectorStatus.Valid;
            if (incidentIndex.HasValue && index > incidentIndex.Value)
            {
                if (!_options.CountPositiveSectorsAfterIncident ||
                    !IsRepresentativePostIncidentSector(raw, incidentIndex.Value, sector))
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
                SessionWeight: GetSessionWeight(session.SessionType),
                IsRepresentative: false,
                IncidentSectorIndex: incidentIndex,
                Sectors: sectorResults,
                SectorCoverage: sectorCoverage,
                NoResultReason: representative
                    ? "InsufficientSectorEvidence"
                    : "NonRepresentativeRun",
                CompetitionClassPriority: session.CompetitionClassPriority);
        }

        var sectorPace = CalculateSectorPace(validScores);
        var evidenceAdjustedSectorPace = AdjustSectorPaceForEvidence(
            sectorPace,
            sectorCoverage,
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
        var fullRunTimeScore = CalculateEffectiveFullRunTimeScore(
            run,
            incidentIndex,
            session.FinishStatistics,
            session.CumulativeStatistics);
        var sectorAndFullRunPace = CombineSectorAndFullRunPace(
            session.SessionType,
            evidenceAdjustedSectorPace,
            fullRunTimeScore);
        var dominanceBonus = !run.IsClassified || session.FinishStatistics is null
            ? 0
            : CalculateDominanceBonus(
                run.FinishTimeSeconds,
                ToFieldStatistics(session.FinishStatistics));
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
            SessionWeight: GetSessionWeight(session.SessionType),
            IsRepresentative: true,
            IncidentSectorIndex: incidentIndex,
            Sectors: sectorResults,
            SectorCoverage: sectorCoverage,
            EvidenceAdjustedSectorPace: evidenceAdjustedSectorPace,
            DominanceBonus: dominanceBonus,
            CompetitionClassPriority: session.CompetitionClassPriority);
    }

    public RiderPaceResult CalculatePace(
        string riderId,
        IEnumerable<RiderSessionPaceResult> sessions,
        int? availableFinalSessions = null,
        CompetitionClassWeighting? competitionClassWeighting = null)
    {
        var validSessions = sessions
            .Where(session =>
                string.Equals(session.RiderId, riderId, StringComparison.OrdinalIgnoreCase) &&
                session.IsRepresentative &&
                session.Pace.HasValue &&
                session.Confidence > 0 &&
                session.SectorCoverage > 0 &&
                session.SessionType is PaceSessionType.Final or
                    PaceSessionType.SemiFinal or
                    PaceSessionType.Qualifying or
                    PaceSessionType.Seeding)
            .ToArray();

        if (validSessions.Length == 0)
        {
            return new RiderPaceResult(riderId, Pace: null, RawPace: null, Confidence: 0, UsedSessions: 0);
        }

        var qualifyingPace = CalculateStagePace(validSessions.Where(session =>
            session.SessionType == PaceSessionType.Qualifying), competitionClassWeighting);
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
            rawPace = CalculatePaceWithFinal(
                finalPace.Pace,
                semiFinalPace?.Pace,
                qualifyingPace?.Pace,
                seedingPace?.Pace);
            highestStage = PaceSessionType.Final;
        }
        else if (semiFinalPace is not null)
        {
            var combinedPace = CalculatePaceWithSemiFinal(
                semiFinalPace.Pace,
                qualifyingPace?.Pace,
                seedingPace?.Pace);
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
                session.Confidence > 0 &&
                session.SectorCoverage > 0)
            .Select(session => new
            {
                Pace = session.Pace!.Value,
                Weight = NormalizeConfidence(session.Confidence) *
                    Math.Pow(
                        Clamp(session.SectorCoverage, 0, 1),
                        _options.CoverageWeightPower) *
                    (competitionClassWeighting?.GetWeight(session.CompetitionClassPriority) ?? 1.0)
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
        return new StagePaceResult(Clamp(pace, 0, 100), weightSum, weightedSessions.Length);
    }

    internal static double CalculatePaceWithFinal(
        double finalPace,
        double? semiFinalPace,
        double? qualifyingPace,
        double? seedingPace)
    {
        var components = new List<WeightedPace>
        {
            new(finalPace, 0.85 * GetSessionWeight(PaceSessionType.Final))
        };
        AddIfPresent(components, semiFinalPace, 0.10 * GetSessionWeight(PaceSessionType.SemiFinal));
        AddIfPresent(components, qualifyingPace, 0.04 * GetSessionWeight(PaceSessionType.Qualifying));
        AddIfPresent(components, seedingPace, 0.01 * GetSessionWeight(PaceSessionType.Seeding));
        return WeightedMeanNormalized(components);
    }

    internal static double CalculatePaceWithSemiFinal(
        double semiFinalPace,
        double? qualifyingPace,
        double? seedingPace)
    {
        var components = new List<WeightedPace>
        {
            new(semiFinalPace, 0.70 * GetSessionWeight(PaceSessionType.SemiFinal))
        };
        AddIfPresent(components, qualifyingPace, 0.25 * GetSessionWeight(PaceSessionType.Qualifying));
        AddIfPresent(components, seedingPace, 0.05 * GetSessionWeight(PaceSessionType.Seeding));
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
        if (cleanSectorCount <= 0 ||
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
        if (cleanSectorCount <= 0 ||
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

    private static void AddIfPresent(List<WeightedPace> components, double? pace, double weight)
    {
        if (pace.HasValue)
        {
            components.Add(new WeightedPace(pace.Value, weight));
        }
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

    private int? DetectIncident(IReadOnlyList<RawSectorEvaluation> sectors)
    {
        var previousPerformance = new List<double>();
        var previousPercentiles = new List<double>();

        for (var index = 0; index < sectors.Count; index++)
        {
            var current = sectors[index];
            if (!current.PerformanceZ.HasValue ||
                !current.Percentile.HasValue ||
                current.Time is null ||
                current.Statistics is null)
            {
                continue;
            }

            if (previousPerformance.Count >= _options.MinimumPreviousSectorsForIncident)
            {
                var baselinePerformance = Median(previousPerformance.TakeLast(3));
                var baselinePercentile = Median(previousPercentiles.TakeLast(3));
                var performanceDrop = baselinePerformance - current.PerformanceZ.Value;
                var percentileDrop = baselinePercentile - current.Percentile.Value;
                var absoluteLoss = current.Time.Value - current.Statistics.Median;
                var relativeLoss = absoluteLoss / current.Statistics.Median;

                var extremelySlow = current.SlowZ >= _options.IncidentSlowZThreshold;
                var suddenPerformanceDrop = performanceDrop >= _options.IncidentPerformanceDropThreshold;
                var largeAbsoluteLoss = absoluteLoss >= _options.MinimumIncidentLossSeconds;
                var hadStrongPreIncidentPace =
                    baselinePercentile >= 0.70 ||
                    baselinePerformance >= 0.5;
                var additionalSignal =
                    percentileDrop >= _options.IncidentPercentileDropThreshold ||
                    relativeLoss >= _options.IncidentRelativeLossThreshold ||
                    current.Percentile.Value <= 0.05;

                if (extremelySlow &&
                    suddenPerformanceDrop &&
                    largeAbsoluteLoss &&
                    hadStrongPreIncidentPace &&
                    additionalSignal)
                {
                    return index;
                }
            }

            previousPerformance.Add(current.PerformanceZ.Value);
            previousPercentiles.Add(current.Percentile.Value);
        }

        return null;
    }

    private bool IsRepresentativePostIncidentSector(
        IReadOnlyList<RawSectorEvaluation> sectors,
        int incidentIndex,
        RawSectorEvaluation current)
    {
        if (!current.Score.HasValue ||
            !current.Percentile.HasValue ||
            !current.PerformanceZ.HasValue ||
            current.Score.Value < _options.PostIncidentMinimumPositiveScore)
        {
            return false;
        }

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
            return false;
        }

        var baselineScore = Median(baselineSectors.Select(sector => sector.Score!.Value));
        var baselinePercentile = Median(baselineSectors.Select(sector => sector.Percentile!.Value));
        var baselinePerformance = Median(baselineSectors.Select(sector => sector.PerformanceZ!.Value));

        return baselineScore - current.Score.Value <= _options.PostIncidentMaximumScoreDrop &&
            baselinePercentile - current.Percentile.Value <= _options.PostIncidentMaximumPercentileDrop &&
            baselinePerformance - current.PerformanceZ.Value <= _options.PostIncidentMaximumPerformanceDrop;
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
            .OrderByDescending(score => score.Score)
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
        return sessionType switch
        {
            PaceSessionType.Final => 8.0,
            PaceSessionType.SemiFinal => 2.0,
            PaceSessionType.Qualifying => 1.50,
            PaceSessionType.Seeding => 0.60,
            PaceSessionType.TimedTraining => 0.00,
            PaceSessionType.Training => 0.00,
            _ => 0.00
        };
    }

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
