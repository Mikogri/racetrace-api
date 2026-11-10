namespace RaceTrace.Api.Services;

internal sealed class IncidentDetectionOptions
{
    public double InvalidShortRatio { get; init; } = .25;
    public double CheckpointShortRatio { get; init; } = .65;
    public double CheckpointToleranceSeconds { get; init; } = 1;
    public double CheckpointToleranceRatio { get; init; } = .05;
    public int ReferenceSectors { get; init; } = 3;
    public double ScaleFloor { get; init; } = .02;
    public double CleanDeviation { get; init; } = 1.5;
    public double LargeLossSeconds { get; init; } = 4;
    public double LargeLossDeviation { get; init; } = 1.5;
    public double CombinedEvidence { get; init; } = 4;
    public int LargeLossCleanSectors { get; init; } = 2;
    public double IsolatedLossSeconds { get; init; } = 2;
    public double IsolatedDeviation { get; init; } = 2;
    public int IsolatedCleanSectors { get; init; } = 3;
    public double IsolatedRankDrop { get; init; } = .5;
    public int TopFieldMinimumSize { get; init; } = 6;
    public int TopReferenceCount { get; init; } = 3;
    public double TopMedianRank { get; init; } = .15;
    public double TopAdjacentRank { get; init; } = .2;
    public double TimeEvidenceDeviation { get; init; } = 1.5;
    public double FieldOnlyLossSeconds { get; init; } = 15;
    public double FieldOnlyLossRatio { get; init; } = .5;
    public double RecoveryLogTolerance { get; init; } = .05;
    public double ExtremeLossSeconds { get; init; } = 10;
    public double ExtremeLossRatio { get; init; } = .25;
}

// Existing defaults relocated verbatim. Override objects remain supported by calculators.
internal sealed class SessionWeightOptions
{
    // Effective round-impact weights used by Pace, Consistency and ResultScore.
    // No additional per-stage multipliers are applied later in the pipeline.
    public double Final { get; init; } = 4.0;
    public double SemiFinal { get; init; } = 2.0;
    public double Qualifying1 { get; init; } = 1.5;
    public double Qualifying2 { get; init; } = 1.5;
    public double Qualifying { get; init; } = 1.5;
    public double Seeding { get; init; } = 0.6;
    public double TimedTraining { get; init; } = 0.0;
    public double Training { get; init; } = 0.0;

    // If finals and earlier stages are both present, finals collectively receive
    // this share and all earlier stages proportionally share the remainder.
    public double FinalTargetImpact { get; init; } = 0.85;

    public double GetWeight(PaceSessionType sessionType)
    {
        return sessionType switch
        {
            PaceSessionType.Final => Final,
            PaceSessionType.SemiFinal => SemiFinal,
            PaceSessionType.Qualifying1 => Qualifying1,
            PaceSessionType.Qualifying2 => Qualifying2,
            PaceSessionType.Qualifying => Qualifying,
            PaceSessionType.Seeding => Seeding,
            PaceSessionType.TimedTraining => TimedTraining,
            PaceSessionType.Training => Training,
            _ => 0.0,
        };
    }

    public double[] ApplyFinalTargetImpact(
        IReadOnlyList<PaceSessionType> sessionTypes,
        IReadOnlyList<double> weights
    )
    {
        if (sessionTypes.Count != weights.Count)
        {
            throw new ArgumentException("Session types and weights must have equal lengths.");
        }

        var finalWeight = Enumerable
            .Range(0, weights.Count)
            .Where(index => sessionTypes[index] == PaceSessionType.Final)
            .Sum(index => Math.Max(0, weights[index]));
        var earlierWeight = Enumerable
            .Range(0, weights.Count)
            .Where(index => sessionTypes[index] != PaceSessionType.Final)
            .Sum(index => Math.Max(0, weights[index]));
        if (finalWeight <= 0 || earlierWeight <= 0)
        {
            return weights.ToArray();
        }

        return Enumerable
            .Range(0, weights.Count)
            .Select(index =>
                sessionTypes[index] == PaceSessionType.Final
                    ? weights[index] * FinalTargetImpact / finalWeight
                    : weights[index] * (1.0 - FinalTargetImpact) / earlierWeight
            )
            .ToArray();
    }
}

internal sealed class PaceOptions
{
    public SessionWeightOptions SessionWeights { get; init; } = new();

    // Maximum final round rating for a rider who completed qualifying but did
    // not advance to a final that took place.
    public double NonFinalistMaxScore { get; init; } = 82.0;

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

    // Minimum baseline size for the legacy post-incident recovery fallback.
    public int MinimumPreviousSectorsForIncident { get; init; } = 1;

    // Supporting stable-prefix guard for uncertain sustained slowdown, not the
    // strong-incident gates (defined together in SectorIncidentDetector).
    public double IncidentPerformanceDropThreshold { get; init; } = 4.0;

    // Relative loss required by the uncertain sustained-slowdown guard.
    public double IncidentRelativeLossThreshold { get; init; } = 0.10;

    // Absolute-loss guard for displaced checkpoints and uncertain slowdown.
    public double MinimumIncidentLossSeconds { get; init; } = 3.0;

    // Określa, czy pozytywne i miarodajne sektory po incydencie mogą być liczone.
    // false odrzuca wszystkie sektory po incydencie, true stosuje poniższe progi powrotu.
    public bool CountPositiveSectorsAfterIncident { get; init; } = true;

    // Minimalny wynik sektora po incydencie uznawany za pozytywny. Wyższa wartość
    // przywraca tylko mocniejsze sektory; niższa dopuszcza tempo bliższe środkowi stawki.
    public double PostIncidentMinimumPositiveScore { get; init; } = 50.0;

    // A post-incident sector close to the field median is useful evidence even
    // when it is slower than the rider's exceptional pre-incident baseline.
    public double PostIncidentMinimumPerformanceZ { get; init; } = -1.0;

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

internal sealed class ConsistencyOptions
{
    public double VariationAtHalfScore { get; init; } = 0.06;

    public double ConfidenceComparisonScale { get; init; } = 20.0;

    // Reliability is the primary signal: frequent incidents must dominate the score.
    public double IncidentReliabilityWeight { get; init; } = 0.40;

    // Reaching the final is the second-strongest measure of sporting reliability.
    public double FinalQualificationWeight { get; init; } = 0.35;

    // Direct Q1 qualification is preferred, while advancing through Q2 remains positive.
    public double QualificationQualityWeight { get; init; } = 0.05;

    // This component measures only position variation, not whether the position was good.
    public double PositionRepeatabilityWeight { get; init; } = 0.20;

    // Higher values make each incident reduce Incident Reliability more aggressively.
    public double IncidentRateExponent { get; init; } = 1.20;

    // Prevents one incident in a short sample from collapsing reliability to zero.
    // As evidence grows, the observed incident rate increasingly drives the score.
    public double IncidentToleranceRuns { get; init; } = 2.0;

    // Maximum additional loss for a sustained pattern of repeated incidents.
    // The actual penalty uses incident frequency, not an absolute career count.
    public double MaximumRepeatedIncidentPenalty { get; init; } = 60.0;

    public double RepeatedIncidentPenaltyExponent { get; init; } = 1.15;

    // Score assigned to an event where the rider reached the final through Q2.
    public double Q2QualificationScore { get; init; } = 75.0;

    // Final incidents drive sporting reliability; incidents in earlier sessions
    // remain supporting evidence but cannot dilute repeated final crashes.
    public double FinalIncidentReliabilityShare { get; init; } = 0.85;

    // Robust normalized-position deviation that produces 50 Position Repeatability.
    public double PositionVariationAtHalfScore { get; init; } = 0.12;

    // Keeps the position signal resistant to one extreme result without hiding
    // repeatable smaller changes around the rider's typical position.
    public double RobustPositionDeviationWeight { get; init; } = 0.70;

    public double OutlierReferenceQuantile { get; init; } = 0.75;

    public double MinimumOutlierReferencePercentile { get; init; } = 0.60;

    public double OutlierPercentileDropThreshold { get; init; } = 0.35;
}

internal sealed class RatingOptions
{
    public double PaceWeight { get; init; } = 0.70;
    public double ConsistencyWeight { get; init; } = 0.15;
    public double ResultScoreWeight { get; init; } = 0.15;
    public double RatingContrast { get; init; } = 1.20;
}

internal sealed class ResultScoreOptions
{
    // Anchors the progressive position curve. Lower values create larger gaps
    // between the winner and the rest of the podium.
    public double SecondPlaceScore { get; init; } = 96.0;
    public double ConfidenceWeightScale { get; init; } = 2.5;
}

internal sealed record PodiumPredictionOptions
{
    public string ModelVersion { get; init; } = "ridge-monte-carlo-v1";

    public double RecentFormDecay { get; init; } = 0.70;

    public double AffinityShrinkageK { get; init; } = 3.0;

    public double PressureShrinkageK { get; init; } = 5.0;

    public double WchShrinkageK { get; init; } = 3.0;

    public double IncidentShrinkageK { get; init; } = 8.0;

    public double IncidentPriorProbability { get; init; } = 0.08;

    public int MonteCarloIterations { get; init; } = 50_000;

    public int StudentTDegreesOfFreedom { get; init; } = 5;

    public int RandomSeed { get; init; } = 20260824;

    public double MinimumUncertainty { get; init; } = 1.25;

    public double MaximumUncertainty { get; init; } = 12.0;

    public double WchUncertaintyMultiplier { get; init; } = 1.15;

    public double ConsistencyUncertaintyRange { get; init; } = 5.0;

    public double ConfidenceUncertaintyRange { get; init; } = 4.0;

    public double EvidenceUncertaintyScale { get; init; } = 2.0;

    public double MaximumIncidentProbability { get; init; } = 0.95;

    public double IncidentPerformanceLoss { get; init; } = 18.0;

    public double IncidentTailLoss { get; init; } = 12.0;

    public double MaximumPointsPerRound { get; init; } = 250.0;

    public double FinalRoundTitleLeverageBoost { get; init; } = 0.25;

    // Versioned ridge coefficients. Runtime never trains the model.
    public double RatingCoefficient { get; init; } = 0.58;

    public double PaceCoefficient { get; init; } = 0.42;

    public double RecentFormCoefficient { get; init; } = 0.20;

    public double TrackAffinityCoefficient { get; init; } = 0.16;

    public double ConditionsAffinityCoefficient { get; init; } = 0.08;

    public double WeekendPerformanceCoefficient { get; init; } = 0.32;

    public double WchDeltaCoefficient { get; init; } = 0.12;
}
