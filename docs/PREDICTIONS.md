# Predykcje podium i Fantasy

Oba moduły korzystają z istniejących metryk sportowych i wspólnego silnika wyścigu. Podium zwraca prawdopodobieństwa, a Fantasy przelicza rozkład miejsc na oczekiwane punkty i dobiera skład w budżecie. Są to modele warunkowe dla wybranej puli zawodników, nie gwarancja rzeczywistego udziału ani wyniku.

## Podium Predictor

`PodiumPredictionFeatureBuilder` przygotowuje Rating, Pace, aktualną formę, dopasowanie do trasy i warunków, tempo bieżącego weekendu, różnicę osiągów w WCH oraz reakcję na presję klasyfikacji. Powiązane shrinkaże ograniczają wpływ małych próbek: trasa i WCH mają skalę 3, presja 5, a incydenty 8 z priorem 0,08. `RecentFormDecay=0.70` osłabia starsze dowody. Rozdzielenie kategorii jest zachowane.

W builderze prawdopodobieństwo incydentu jest estymowane z historycznych DNF: `(DNF + 8×0.08)/(starty + 8)`, z zabezpieczeniem mianownika. Nie jest to częstość wszystkich sektorów oznaczonych przez detektor Pace. Predictor odrzuca kategorie juniorskie; kandydaci są ograniczani przez confidence i pulę rankingową, więc prawdopodobieństwa nie opisują automatycznie całej rzeczywistej listy startowej.

`PodiumPredictionCalibration` wyznacza bazę z `0.58 × Rating + 0.42 × Pace`, a następnie dodaje formę (0,20), affinity trasy (0,16), warunków (0,08), performance weekendu (0,32), różnicę WCH (0,12) i iloczyn reakcji na presję z title leverage. Nazwa modelu `ridge-monte-carlo-v1` nie oznacza trenowania regresji przy każdym odczycie: współczynniki są stałymi kodu.

Confidence przesuwa oczekiwany performance w stronę średniej pola: `średnia + Confidence/100 × (surowy performance−średnia)`. Niepewność wynosi `1.25 + (1−Consistency/100)×5 + (1−Confidence/100)×4 + 2/sqrt(max(1,EvidenceCount))`, z mnożnikiem WCH 1,15 i ograniczeniem 1,25–12. Maksymalne prawdopodobieństwo incydentu to 0,95.

`MonteCarloRaceSimulator` wykonuje domyślnie 50 000 iteracji z seedem 20260824. Performance jest losowany z rozkładu Studenta o pięciu stopniach swobody; niezależny los incydentu odejmuje `18 + 12 × abs(StudentT)`. Kolejność symulacji daje prawdopodobieństwo zwycięstwa, podium, Top 5 i rozkład miejsc. Nie jest to symulacja rzeczywistych czasów sektorów ani wierna rekonstrukcja DNF; modeluje ciężki ogon gorszych osiągów. Publiczny predictor ma Output Cache 60 s.

Historyczny test sześciu wariantów na 40 zdarzeniach kategorii 2022–2024 nie dał podstaw do automatycznego zastąpienia produkcyjnego modelu. Wyłączał cały oceniany weekend, więc nie jest dokładnym replayem endpointu korzystającego z aktualnych dowodów weekendowych. [Raport](archive/podium-backtest-2026-09-15.md) zawiera protokół i wyniki; nie należy przedstawiać ich jako bieżącej skuteczności na nowych sezonach.

## Fantasy: reguły i punkty

`FantasyRuleCatalog` rejestruje zweryfikowane reguły dla 2026: `pinkbike-dh-2026-v1`. Nieznany sezon nie dostaje automatycznie punktacji 2026. `FantasyRules2026` przechowuje pełne, odrębne tabele finałów mężczyzn (30 pozycji) i kobiet (15), bonus Q1 100/75/50 oraz skład 4 mężczyzn + 2 kobiety za 1 500 000 USD. Źródła oficjalnej grafiki i zasad są zapisane w tej klasie; punktów nie interpoluje się z samych miejsc podium.

`FantasyRacePrediction` jest adapterem istniejącego modelu, a nie nowym kalkulatorem tempa. Przygotowanie historii wyłącza target weekend i późniejsze dane. Historyczne symulacje mają 10 000 iteracji, bieżąca prognoza 50 000. Kalibracja `fantasy-empirical-blend-v3` osobno dla kategorii i etapu miesza rozkład silnika z modelem wymiennych riderów. Wymaga co najmniej dwóch historycznych wydarzeń, a współczynnik mieszania wybiera z siatki 0–1 co 0,01 przez minimalizację log-loss na wcześniejszych obserwacjach.

W modelu bazowym liczba sklasyfikowanych ma rozkład dwumianowy z historycznym udziałem niepunktujących `q`; prawdopodobieństwo pozycji `k` dla ridera wynosi `P(K≥k)/N`. Pozostała masa reprezentuje wynik niepunktujący. Kalibracja zachowuje sumę prawdopodobieństw i ograniczenie obsady miejsc; nie dodaje niezależnej szansy wygranej każdemu zawodnikowi.

Predicted Points to `suma P(miejsce) × punkty`, osobno dla finału i bonusu Q1, następnie razem. Predicted Position jest oczekiwanym miejscem warunkowo przy klasyfikacji, a Value relacją prognozowanych punktów do ceny. Nie są to oficjalne punkty ani procent szans na opłacalność. Reguły DNS/DSQ dotyczące bonusu nie mają tak mocnego potwierdzenia źródłowego jak sprawdzony przykład DNF z wcześniejszym bonusem Q1; niejednoznaczne historyczne przypadki są wyłączane z oceny łącznych punktów.

## Best Team i dostępność zawodników

`FantasyOptimizer` rozważa wszystkie pary kobiet i czwórki mężczyzn; najlepsza dopuszczalna para w pozostałym budżecie jest wyszukiwana przez prefiksy cenowe. To dokładne maksimum sumy EV dla dostarczonych kandydatów, nie greedy według Value i nie gwarancja najlepszego rzeczywistego składu.

Kandydat potrzebuje ceny, jednoznacznego mapowania i prognozy. Zgłoszona kontuzja wyklucza automatyczny wybór, ale rider pozostaje widoczny w tabeli. Potwierdzona lista startowa nie jest obowiązkowa: bez niej używany jest katalog Pinkbike z ostrzeżeniem o niepotwierdzonym udziale.

Dla rundy live `FantasyTrainingParticipants` próbuje użyć rosteru Timed Training ChronoRace albo odpowiedniej fazy Tissot. Dopasowanie bierze pod uwagę kategorię i znormalizowaną tożsamość, bez zgadywania niejednoznacznych nazw. Lista skonfigurowana w `Fantasy:Events` ma pierwszeństwo i wymaga źródła oraz czasu pobrania. Częściowa lista finisherów nie jest kompletnym rosterem. Ograniczenie dotyczy wyboru Best Team, nie usuwania rywali z modelu wyścigu. Brak dostępnego rosteru nie zeruje rekomendacji.

## Synchronizacja cen i ręczny import

`PinkbikeFantasyImporter` najpierw próbuje zwykłego HTTP oficjalnej strony Athletes. Pobranie lub wstępna walidacja może uruchomić fallback do odizolowanego headless Chromium. HTTP ma najwyżej 10 s i jedną trzecią wspólnego budżetu operacji; anulowanie nie uruchamia fallbacku. Błąd mapowania lub zapisu nie pobiera strony ponownie. Obie ścieżki używają tego samego parsera osadzonego `aAthletesKeyed`, walidacji i mapowania.

Nie korzystamy z osobistego profilu przeglądarki, rozwiązywania CAPTCHA ani udawania, że strona weryfikacyjna jest katalogiem. Pinkbike może blokować obie ścieżki; błąd zachowuje ostatni poprawny snapshot i jego prawdziwy czas pozyskania. Dostępności źródła nie gwarantuje sam kod.

Po buildzie API przeglądarkę instaluje się dla konta usługi:

```powershell
dotnet build racetrace-api/racetrace-api.csproj -c Release
pwsh racetrace-api/bin/Release/net10.0/playwright.ps1 install chromium
```

Na Linux potrzebne są również zależności systemowe Chromium. Po aktualizacji Playwright może być potrzebna ponowna instalacja; publikacja DLL sama nie dostarcza przeglądarki.

Administrator może wysłać świeży, niezmieniony zapis strony z JSON-em jako body `POST /api/admin/fantasy/sync-html`, `Content-Type: text/html`, do 8 MB. To jawny import HTML, nie CSV ani obrazka. Parser sprawdza strukturę, sezon, ceny i nadmierny spadek liczby riderów, ale nie potwierdza kryptograficznie pochodzenia pliku. `sync-html` zapisuje snapshot bez natychmiastowego przeliczenia składu; następnie wywołuje się `POST /api/admin/fantasy/prepare`. Automatyczny import można uruchomić przez `POST /api/admin/fantasy/sync?prepare=true`.

## Przygotowanie, trwałość i wiele instancji

Publiczne GET-y nie uruchamiają scrapingu ani symulacji. `FantasySyncHostedService` obsługuje przygotowanie w tle. W śledzonym `appsettings.json` harmonogram jest włączony z interwałem 6 h, a kontrola kontekstu live ma domyślnie 15 min. Błąd cen nie musi blokować niezależnego odświeżenia prognozy. Uruchomienie API może więc wykonać skonfigurowane prace, w przeciwieństwie do eksportera kontraktów.

Domyślne lokalne snapshoty znajdują się w ignorowanym `App_Data/Fantasy`; wdrożenie wymaga trwałego wolumenu i kopii zapasowej. Upływ interwału sam nie kasuje zgodnej prognozy. Zmiana rundy, modelu, reguł, konfiguracji startowej albo rewizji źródła ją unieważnia. Starsze ceny mogą zostać użyte z ostrzeżeniem wyłącznie dla tej samej rundy. Przy wyłączonej spójności rewizji stosowany jest starszy kontrolny fingerprint liczebności i najnowszych aktualizacji źródeł, cache'owany przez 60 s; nie jest to gwarancja wykrycia dowolnej zmiany źle utrzymywanego `updated_at`.

`Fantasy:DistributedCoordinationEnabled` domyślnie jest wyłączone. Wymaga jawnego wdrożenia `20261010210124_fantasy_job_coordination.sql` i zgodnej flagi na wszystkich workerach oraz czytelnikach. W tym trybie snapshoty trafiają do PostgreSQL, a lease z rosnącym fencing tokenem chroni publikację: domyślnie 2 min z heartbeat co 30 s. Pobieranie zewnętrzne i obliczenia są poza transakcją; walidacja własności, publikacja i zakończenie współdzielą transakcję. Nieaktualny worker nie może nadpisać następcy.

Admin endpointy przyjmują opcjonalny `Idempotency-Key` długości 1–180 drukowalnych znaków. Wspólny cykl nie wykonuje ponownie zakończonej publikacji; konflikt zwraca 409. Nie gwarantuje to jednokrotnego pobrania HTTP po awarii. Prywatne `racetrace_jobs.leases` i `runs` przechowują dzierżawy i historię zakończeń. Usunięcie historii zakończeń usuwa idempotencję tych identyfikatorów.

Lokalna historia nie jest automatycznie przenoszona do bazy. Przed przełączeniem trzeba przygotować i sprawdzić wspólny snapshot, zachować pliki do rollbacku i nie mieszać trybów za jednym load balancerem. Powrót flagi przywraca lokalne pliki, nie najnowszy snapshot bazy. Bez trybu rozproszonego blokady są lokalne dla procesu, więc nie należy uruchamiać konkurujących synchronizatorów nad jednym katalogiem.

Przygotowane artefakty zachowują fingerprint zależności, konfigurację kalibracji, wersję modelu, czas obliczenia, cutoff danych i seed/liczbę symulacji. Stare prognozy bez takiego fingerprintu nie otrzymują fikcyjnego pochodzenia; włączenie nowych mechanizmów wymaga ponownego przygotowania prognoz, nie ponownego importu wszystkich wyników sportowych.

## Interpretacja i badania

Dokładność optimizera nie dowodzi jakości prognozy. Skuteczność ocenia się chronologicznie, bez danych target weekendu w treningu. Backtest budżetu wymaga rzeczywistego kompletnego snapshotu cen dostępnego przed startem, a dzisiejszy import historycznych cen nie dowodzi ich ówczesnej dostępności. Brak takich cen oznacza ocenę samych prognoz, nie salary optimization, oracle ani regret.

Historyczne wyniki są w [raporcie Fantasy](archive/fantasy-backtest-2026-09-16.md). To niewielka, współzależna próba zdarzeń, nie bieżący certyfikat trafności. Parametry i wersje sportowych metryk opisuje [ALGORITHMS.md](ALGORITHMS.md); transport, tokeny i kontrakty — [API.md](API.md).
