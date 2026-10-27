# Algorytmy sportowe

Pace, Consistency, ResultScore i Rating są liczone w API z wybranego zakresu danych. Nie są stałymi ocenami zapisanymi przy zawodniku. Porównanie ma sens dla tego samego kontekstu kategorii, sesji i filtrów. Normalny ranking i profil operują na wybranej kategorii; nie wolno niejawnie łączyć juniorskich i elitarnych pól odniesienia.

Źródłem parametrów jest `Services/AlgorithmOptions.cs`, a aktywnych konfiguracji i zależności — `Infrastructure/Calculations/AlgorithmCatalog.cs`. Poniższe wartości opisują domyślną konfigurację kodu, nie dowolny obiekt opcji przekazany przez test. Nazwy modeli transportowych zawierające `Precomputed` nie oznaczają, że wynik kalkulatora pochodzi z trwałego cache: bieżący odczyt odtwarza go z wejść w API.

## Wagi sesji i klas

`SessionWeightOptions` przypisuje finałowi wagę 4, półfinałowi 2, Q1/Q2/zwykłemu Quali po 1,5, Seeding 0,6, a treningom 0. Gdy występują zarówno finały, jak i wcześniejsze etapy, `FinalTargetImpact=0.85` daje finałom łącznie 85% wpływu. Pozostałe etapy dzielą 15% proporcjonalnie do swoich wag. Nie jest to prosta średnia wszystkich startów; szczegóły łączenia Pace są poniżej.

`CompetitionClassWeighting` wyznacza wagę jako `maksymalny aktywny priorytet / priorytet klasy`; brak poprawnego priorytetu daje 1. Publiczne Pace i Consistency powstają bez ważenia klasami. API osobno przygotowuje składowe dla Ratingu; klasa wpływa na jego Pace i ResultScore. Obecny `DownhillConsistencyCalculator` przyjmuje parametr ważenia klas, ale go nie używa — nie należy dokumentować nieistniejącego wpływu.

## Pace

Pace ocenia względne tempo na tle zawodników tej samej sesji. `PrepareSession()` przygotowuje populacje sektorów, sumy narastające, rangi i statystyki wspólne dla wszystkich przejazdów. Brakujący checkpoint nie jest zastępowany zerem ani dzielony na wymyślone sektory. Nieważne, niedodatnie i niefinitywne czasy odpadają.

Dla czasów sektora kalkulator wyznacza medianę logarytmów i odporną skalę `max(1.4826 × MAD(log(czas)), 0.003)`. Szybszy czas daje dodatnie:

```text
z = (mediana(log(czasów pola)) − log(czas ridera)) / skala
percentyl = 1 − (ranga − 1) / (liczba zawodników − 1)
rankScore = 100 × percentyl^1.60
timeScore = 100 × sigmoid(1.25 × z − (−0.05))
rawSector = 0.20 × rankScore + 0.75 × timeScore + winBonus
```

Te dwie wagi celowo nie sumują się do 1. Bonus za wygrany sektor dochodzi do 5 i nasyca się wykładniczo wraz z przewagą nad drugim czasem, znormalizowaną skalą logarytmiczną pola. Równe czasy dzielą rangę. Sektor wymaga co najmniej trzech poprawnych pomiarów. Wynik powyżej 75 przechodzi dodatnią kalibrację `75 + 25 × ((wynik−75)/25)^0.60`, po ograniczeniu do skali 0–100.

Średnie sektorowe waży pierwiastek z mediany czasu sektora, aby dłuższe odcinki miały większy, lecz nie liniowy wpływ. Przy mniej niż czterech ocenionych sektorach używana jest cała średnia. Od czterech: 70% stanowi średnia najlepszej połowy sektorów (liczba zaokrąglona w górę), a 30% średnia wszystkich. Zaokrąglenie klucza sortowania do dziewięciu miejsc stabilizuje wybór przy mikroróżnicach transportu; samych wyników nie zaokrągla przed agregacją.

Co najmniej dwa użyteczne sektory są potrzebne do Pace sesji. Wartości powyżej 50 są osłabiane do `50 + (Pace−50) × sqrt(pokrycie) × min(1, liczba sektorów/4)`. Przy bezpiecznej korekcie incydentu wykluczony sektor nie jest ponownie karany jako brak pokrycia Pace; Confidence nadal korzysta tylko z rzeczywistych dowodów.

Top speed może dodać 2% dodatniej różnicy między SpeedScore a sektorowym Pace; nie obniża wyniku. Wynik pełnego czasu, liczony tą samą sigmoidą względem rozkładu mety, łączy się z sektorowym Pace w proporcji 15/85 dla finału, 10/90 dla półfinału i 5/95 dla kwalifikacji. Jeśli go brakuje, pozostaje samo Pace sektorowe. Przy incydencie kalkulator używa bezpiecznie skorygowanej mety albo dopuszczalnego czystego prefiksu; nie wymyśla oficjalnego finiszu. Osobny bonus dominacji najszybszego sklasyfikowanego przejazdu w analitycznym polu ma maksimum 3, próg relatywnej przewagi 0,0025 i skalę nasycenia 0,006. Dawne stałe bonusy za samo zwycięstwo i `FinishPositiveGain` pozostają w opcjach, lecz nie sterują tym mieszaniem.

Sesje są grupowane w finały, półfinały, kwalifikacje i seeding. W ramach etapu używana jest średnia potęgowa z `TempoPower=5`, ważona `pokrycie²` i ewentualną wagą klasy. Gdy są finały, ich etap dostaje 85%, a wcześniejsze etapy dzielą pozostałe 15%. Bez finału półfinał może być podniesiony przez wcześniejsze etapy najwyżej o 3 surowe punkty. Bez półfinału używany jest etap kwalifikacji, a bez niego seeding.

Najwyższy osiągnięty etap osłabia część ponad 50 współczynnikiem 1 dla finału, 0,96 dla półfinału, 0,90 dla kwalifikacji i 0,80 dla seeding. Następnie działa kalibracja powyżej 55: `55 + 45 × ((Pace−55)/45)^0.50`. Górna korekta dodaje do 1,5 punktu, płynnie narasta między 60–70 i wygasa między 95–100. Końcowe limity bez finału wynoszą 90/82/75 dla półfinału/kwalifikacji/seeding; przy najwyżej czterech użytecznych sektorach są ostrzejsze: 78/72/68.

## Detekcja incydentów

`SectorIncidentDetector` analizuje czasy odcinków, nie skumulowane gapy. Rozdziela wykrycie anomalii, jej silną klasyfikację i możliwość rekonstrukcji czasu. `LossSeconds` jest oszacowaniem straty w sekundach; `RobustDeviation` jest bezwymiarowym odchyleniem, nie sekundami ani prawdopodobieństwem wypadku. Wszystkie odniesienia pola muszą dotyczyć tej samej sesji i kategorii.

Podstawowa ścieżka wymaga lokalnej nadwyżki co najmniej 4 s z dowodem czasowym i pozycyjnym albo izolowanej straty co najmniej 2 s przy potwierdzonym tempie czołówki po obu stronach sektora. Używa mediany najbliższych sektorów i rzeczywistego rozrzutu pola oraz profilu ridera. Sama różnica miejsc nie wystarcza.

Uzupełniająca ścieżka obejmuje tylko ekstremalne straty: jednocześnie co najmniej 10 s, 25% oczekiwanego czasu i odchylenie co najmniej 1,5. Odniesieniem jest mediana relacji czas/mediana pola na pozostałych wiarygodnych sektorach, nie najlepszy sektor. Rozrzut nie jest sztucznie ograniczany: normalne różnice mocnych stron i szerokie pole pozostają częścią niepewności. Co najmniej połowa dostępnych odniesień musi być zgodna z profilem w tolerancji logarytmicznej 0,05. Badany sektor nie wyznacza własnego odniesienia; zamrożona lista odniesień pomija wcześniej silnie oznaczone sektory.

Detekcja ekstremalnej straty nie wymaga dwóch innych pomiarów. Przy zerze lub jednym odniesieniu osobistym wymaga co najmniej 15 s i 50% straty, bez możliwości rekonstrukcji mety. Przy braku odniesień używa mediany pola. Pole nadal musi mieć minimum trzy poprawne pomiary. Rangi nie zastępują brakujących dowodów czasowych i nie są przeliczane na wymyślony oczekiwany czas.

Ekstremalna strata może być wykryta bez późniejszego powrotu do tempa, ale wtedy nie uprawnia do korekty mety. Słaba sekwencja miejsc, pojedynczy wyjątkowo szybki sektor oraz stopniowe różnice tempa nie są automatycznie incydentem ani ostrzeżeniem obniżającym Confidence. Niezależna ścieżka `PossibleIncident` pozostaje dla utrzymującego się spowolnienia po stabilnym początku; nie usuwa sektorów z punktacji.

Silne incydenty i podejrzane błędy pomiaru nie otrzymują sektorowego Pace. Możliwe incydenty nie są automatycznie czyszczone. Poprawne sektory po odzyskaniu tempa mogą ponownie zasilać Pace. Nienaturalnie krótki sektor i kompensujący długi są sprawdzane jako problem jakości pomiaru, nie automatyczny dowód wypadku.

Korekta mety ma dwa przebiegi: najpierw diagnozuje się całe pole z oryginalnych danych, potem od sklasyfikowanych, kompletnych przejazdów odejmuje wyłącznie dodatnią oszacowaną stratę silnego incydentu z powrotem do tempa albo incydentu końcowego. Muszą pozostać co najmniej dwa czyste sektory i połowa trasy; braki, błędy pomiaru, niejednoznaczne sygnały lub niezgodność sumy sektorów z metą blokują rekonstrukcję. Dopiero wtedy powstaje wspólne skorygowane pole odniesienia. Nie zmienia się oficjalnych czasów, miejsc ani punktów i nie dopisuje mety DNF/DNS/DSQ. Usunięcie wymogu dwóch odniesień dla detekcji nie usuwa zabezpieczeń rekonstrukcji.

Detektor nie zna nazwisk ani etykiet z audytowego CSV. Nie potwierdza przyczyny straty. Brak pomiaru po zdarzeniu, mała fizyczna kraksa bez wyraźnej straty lub uszkodzenie od samego startu mogą nie dać zlokalizowanego sygnału. Priorytetem jest wystarczający dowód anomalii, nie maksymalne pokrycie niepełnej listy incydentów. Potwierdzone dodatnie przypadki bez pełnej listy czystych przejazdów nie pozwalają obliczyć precision ani odsetka fałszywych alarmów. DNF jest niezależnym dowodem nieukończenia przejazdu, nie pomiarem miejsca incydentu. Wersja detektora `1.2.0` zmienia fingerprint zależnych kalkulacji; pakiety wejściowe w bazie nie wymagają przebudowy. Dawne eksperymenty są w [archiwum](archive/incident-experiments-2026.md), a nie w regułach produkcyjnych.

Audyt tylko do odczytu można uruchomić z repozytorium API:

```powershell
dotnet run --project tools/racetrace-api.PodiumBacktest -c Release -- --incident-audit --api racetrace-api --year 2026 --output artifacts/incident-audit
```

`--offline` odtwarza zapisane pomiary i statystyki, odrzucając zapisane wcześniej `PaceFinish` i przygotowane anomalie. Referencje zależne od starej wersji detektora muszą zostać obliczone ponownie; nie jest to przebudowa pakietów wejściowych w bazie. `--confirmed tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv` porównuje z dostarczonymi etykietami, a `--verify-details` wykonuje dodatkowe odczyty detali. Audyt odróżnia zgodność ścieżek obliczeń od zgodności z rzeczywistymi zdarzeniami. Brakujący pakiet zatrzymuje ten konkretny probe; nie należy utożsamiać tego z brakiem produkcyjnego fallbacku API. Artefakty pozostają poza Git.

### Offline multi-season validation

The canonical study now runs the actual C# detector, using the five external Markdown source lists and the existing sporting-only 2026 CSV:

```powershell
dotnet run --project tools/racetrace-api.PodiumBacktest -c Release -- --incident-study --input artifacts/incident-multiseason --documents C:/path/to/source-documents --output artifacts/incident-csharp --sweep
```

The main report evaluates the fixed production defaults, not a recall-maximizing candidate. Optional threshold experiments and whole-year selection are exploratory only; selection on training years limits alarm counts to 115% of the older detector. This budget is not a runtime quota or a precision estimate. Synthetic controls protect uniform slow profiles, one exceptional sector, gradual variation, wide fields, small terminal losses and displaced checkpoints; calculator regression tests separately protect scoring/evidence. `summary.json` records input hashes, options, yearly transfer checks and the distinct localized-vs-official-DNF counts. `labels.csv` exposes every match/exclusion and `sectors.csv` includes separate seconds and dimensionless-deviation columns. The 2026 list is included; no untouched validation claim is made. No source narratives, links or health data are copied. The study reads local snapshots only and does not update the database or publish changes.

`tools/incident-multiseason.py` is the earlier, offline-only comparison tool, not the implementation used by the API. Both studies consume `inputs-YEAR.json` and `baseline.json`, generated with the audit above using `--name baseline --output artifacts/incident-multiseason/YEAR` for each year 2021–2026. The earlier experiment remains reproducible with:

```powershell
python tools/incident-multiseason.test.py
python tools/incident-multiseason.py --input artifacts/incident-multiseason --documents C:/path/to/source-documents --output artifacts/incident-multiseason/experiment
```

The document directory contains `RaceTrace_Incidents_2021.md` through `RaceTrace_Incidents_2025.md`. Matching uses season, event/class, category, session and an explicit name-alias map; Snowshoe's two 2021 events stay separate. Training, ambiguous sessions and DNS/DSQ are not counted as scored positives; repeated observations of one run count once. The parser does not retain source narratives or URLs. `matching.csv` makes exclusions inspectable; `label-results.csv` compares every observation. These files, input data and generated reports remain outside Git.

Selection compares labelled run coverage subject to an alarm-count budget, with whole-year and chronological transfer checks. Unlabelled runs remain unknown: no precision or false-positive rate is inferred. Hits do not establish the correct sector or physical cause. The 2026 inputs are development-only, not part of the 2021–2025 coverage score. Optional `--guarded-local` restricts local-profile candidates using three fixed prior-feedback checks: both Pierron cases remain detected and Pontvianne's small terminal loss remains unflagged. These checks constrain model eligibility, not the detector's runtime behavior; they are not independent validation. A candidate is not promoted automatically: production parity, finish-correction/ranking impact and review of extra alarms remain separate checks.

## Consistency

Aktualny wynik nie jest dawną regresją pozycji w czasie. Kalkulator łączy cztery składowe:

```text
Consistency = 0.40 × IncidentReliability
            + 0.35 × FinalQualification
            + 0.05 × QualificationQuality
            + 0.20 × PositionRepeatability
```

Przy mniej niż dwóch istotnych przejazdach zwraca brak wyniku, nie sztuczne zero. DNS i DSQ/DQ są ignorowane w istotnych przejazdach, DNF pozostaje dowodem incydentu. Etapy z zerową wagą nie zasilają wyniku. Grupowanie wydarzeń korzysta z dostarczonego `CourseKey`, a nie dowolnego łączenia tras po nazwie.

Niezawodność dla `n` przejazdów i `i` incydentów jest ograniczoną do 0–100 wartością:

```text
100 × (1 − i/(n+2))^1.20 − 60 × (max(0,i−1)/(n+2))^1.15
```

Gdy są finały, 85% tej składowej pochodzi z niezawodności finałów, a 15% ze wszystkich istotnych przejazdów. Incydent oznacza DNF albo flagę `HasIncident`; zwykły wolny wynik nie wystarcza.

`FinalQualification` to procent wydarzeń z wpisem finałowym. `QualificationQuality` daje 100 za finał bez istotnego Q2 i 75 po Q2; wydarzenie bez finału daje 0. DNS w finale nadal dowodzi kwalifikacji. To inna reguła niż licznik rzeczywistych startów finałowych w rankingu.

Powtarzalność bierze sklasyfikowane miejsca z prawidłowym rozmiarem pola i normalizuje je jako `(miejsce−1)/(pole−1)`; jednoosobowe pole daje 0. Po zastosowaniu wag etapów środek stanowi ważona mediana. Odchylenie to `0.70 × 1.4826 × ważona mediana odchyleń + 0.30 × ważone średnie odchylenie bezwzględne`. Wynik wynosi `100 / (1 + (odchylenie/0.12)^2)`. Przy mniej niż dwóch użytecznych pozycjach składowa ma neutralne 50.

Kalkulator dodatkowo raportuje zgodność czasów sektorów na tej samej trasie: odporny rozrzut logarytmów, skalę 0,06 i udział ukończonych prób. Te wyniki nie są bezpośrednią średnią tworzącą końcowe Consistency. Błędny pomiar jest pomijany, a rzeczywista strata incydentu nie jest zastępowana syntetycznym czasem. `IsOutlier` nie stanowi osobnego mnożnika końcowej oceny.

## ResultScore i Rating

ResultScore ocenia miejsca, nie zdobyte oficjalne punkty. Dla sklasyfikowanego pola `N≥2` P1 otrzymuje 100, ostatnie miejsce 0, a wnętrze krzywej:

```text
a = log(0.96) / log(1 − log(2)/log(N))
score(r) = 100 × (1 − log(r)/log(N))^a
```

Kotwica drugiego miejsca wynosi 96, ale w polu dwóch zawodników P2 jest zarazem ostatnie i dostaje 0. DNS i DSQ/DQ są pomijane; DNF otrzymuje 0 z wagą sesji, jeśli pole spełnia wymagania. Agregacja używa wag sesji i klas oraz normalizacji udziału finałów. Confidence ResultScore powstaje z sumy wag **przed** normalizacją: `1−exp(−suma/2.5)`; `UsedSessions` liczy użyte sesje. Oficjalne punkty są odrębnymi faktami bazy.

Surowy Rating to `0.70 × Pace + 0.15 × Consistency + 0.15 × ResultScore`. Przy braku ResultScore pozostałe wagi są renormalizowane. W potoku `PaceService` brak Pace lub Consistency oznacza brak Ratingu. Następnie działa kontrast:

```text
Rating = 50 + 50 × tanh(1.20 × (RawRating−50)/50) / tanh(1.20)
```

Wyniki są ograniczane do 0–100. `PaceService` stosuje dodatkowy limit 82 dla ridera zakwalifikowanego przez logikę `IsNonFinalist` jako nieawansujący do rozegranego finału; nie jest to kara za sam brak danych z przyszłej rundy. Publiczne metryki są zaokrąglane przy budowaniu odpowiedzi, nie przy każdym działaniu pośrednim.

## Confidence i statusy

Confidence oznacza ilość/jakość dowodów, nie szansę wygranej i nie dodatkowy bonus Ratingu. Confidence sesji Pace korzysta wyłącznie z sektorów `Valid`, nie z estymacji ani sektorów po incydencie. Iloczyn stanowią: `1−exp(−liczba/4)`, pierwiastek pokrycia i pierwiastek z ograniczonego `mediana liczebności pola/30`. Wynik jest prezentowany w skali 0–100.

Łączne Evidence sumuje znormalizowane Confidence sesji pomnożone przez wagę etapu i ewentualnie klasy. Surowe Confidence to `100 × (1−exp(−Evidence/4))`, ograniczane dowodami finałowymi. Przy znanej liczbie dostępnych finałów używany jest udział potwierdzonych dowodów względem `liczba finałów × 0.5`, z potęgą 0,75; bez finałowego dowodu limit wynosi `40 / liczba finałów^0.75`. Bez znanej liczby finałów limit stopniowo rośnie od 40 wraz z dowodami finałów (skala 3).

Consistency ma własne Confidence: `1−exp(−(możliwości porównań sektorów + istotne przejazdy + wydarzenia)/20)`, w skali 0–1 wewnątrz kalkulatora. Nie należy mieszać tych skal ani utożsamiać wszystkich pól o nazwie Confidence.

DNS nie daje tempa przejazdu. DNF może dostarczyć rzeczywiste sektory, ale nigdy wymyślonej mety. DSQ/DQ są wykluczane z obliczeń metryk. Liczniki udziału mają osobną semantykę: DNS w finale nie zwiększa `finalsCount`, ale zachowuje kwalifikację; DNF i DSQ zwiększają liczbę rozpoczętych finałów. Ta reguła prezentacyjna nie zmienia kalkulatorów. Część helperów statusów przycina białe znaki, część porównuje tylko bez rozróżnienia wielkości liter — nie należy obiecywać identycznej tolerancji dowolnego uszkodzonego wejścia w każdym kalkulatorze.

## Wersje i interpretacja

Katalog ma obecnie etykiety `1.0.0` dla wag, Pace, ResultScore i Ratingu, `1.1.0` dla incydentów, `1.0.1` dla Consistency, `ridge-monte-carlo-v1` dla podium i `fantasy-empirical-blend-v3` dla Fantasy. Fingerprint obejmuje konfigurację i domknięcie zależności, więc sama etykieta wersji nie identyfikuje kompletnie obliczenia. Zmiana detektora unieważnia tożsamości zależnych obliczeń, ale nie wymaga przebudowy niezależnych pakietów wejściowych. Skala Round Analytics w odpowiedzi ma niezależne `round-analytics-v2`; jej wpis katalogowy nadal ma `1.0.0`.

Stałe i krzywe są decyzjami modelu, nie dowodem naukowej kalibracji. Dawne porównania rankingów i eksperymenty opisują zamrożone dane oraz ówczesny kod; nie potwierdzają obecnej jakości dla każdego zawodnika. Zmiana kalkulatora wymaga testów zgodności albo świadomego zaakceptowania różnic, ale nie przebudowy niezależnych pakietów źródłowych. [PERFORMANCE.md](PERFORMANCE.md) opisuje narzędzia kontroli.
