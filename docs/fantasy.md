# Fantasy — Pinkbike DH 2026

## Zakres i pliki

Moduł jest częścią obecnego API i strony React `/fantasy` (nawigacja przed About).
Nie zmienia kalkulatorów Pace/Consistency/Rating ani dotychczasowych odpowiedzi podium.

- `racetrace-api/Features/Fantasy/Import/`: parser publicznego katalogu, bezpieczne mapowanie, synchronizacja i historia cen.
- `Features/Fantasy/FantasyRules2026.cs`, `FantasyScoring.cs`: oficjalna punktacja i EV.
- `FantasyRacePrediction.cs`, `FantasyCalibration.cs`, `FantasyPreparationService.cs`: adapter istniejącego silnika, kalibracja i walk-forward.
- `FantasyOptimizer.cs`, `FantasyBacktester.cs`: dokładny skład oraz ocena historyczna.
- `FantasyPreparedStore.cs`, `FantasyService.cs`, `FantasySyncHostedService.cs`: trwałe prognozy, odczyt API, opcjonalny harmonogram.
- `Controllers/FantasyController.cs`, `Controllers/AdminFantasyController.cs`, `Models/FantasyDtos.cs`: kontrakty i autoryzacja.
- Frontend: `src/features/fantasy/`, istniejące komponenty tabel, tooltipów, modala, skeletonów, spinnerów i komunikatów.

## Źródła i zasady

Punktacja została odczytana z [oficjalnej grafiki Pinkbike](https://ep1.pinkbike.org/p4pb29633156/p4pb29633156.jpg)
opublikowanej w [How-To 2026](https://www.pinkbike.com/news/pinkbikes-dh-fantasy-league-contest-how-to-2026.html).
Pełne tabele dla mężczyzn i kobiet są **różne**; zapisano wszystkie 30 i 15 miejsc, bez interpolacji.
Bonus Q1: 100/75/50. Skład: czterech mężczyzn, dwie kobiety, budżet 1 500 000 USD.
Nie ma stałego limitu ceny pojedynczego zawodnika.

Ceny pochodzą z [Pinkbike Athletes](https://www.pinkbike.com/contest/fantasy/dh/athletes/).
Parser odczytuje osadzony JSON `aAthletesKeyed` i metadane rund, **nie wykonuje kodu strony**.
Zachowuje identyfikator zewnętrzny, cenę, season points, kontuzję, czas pozyskania oraz udostępnione historyczne ceny i round scores.
Przy niejednoznacznym nazwisku nie wybiera losowego ridera; ręczne mapowania mają pierwszeństwo.
Nie tworzy nowych rekordów w głównej tabeli riders.

Zweryfikowany przykład zachowania DNF: Bode Burke, Andorra 2026, Q1 P2 + DNF w finale = 75 pkt na Pinkbike.
Zachowania bonusu przy DNS/DSQ nie potwierdzono analogicznym przykładem; takie historyczne przypadki z wcześniej zdobytym bonusem są wyłączone z oceny łącznych punktów.

## Predykcja i optymalizacja

1. Wyłącznie wcześniejsze, ukończone sesje EM/EW; brak wyników z prognozowanego weekendu.
2. Obecne `MetricsService`, `PodiumPredictionFeatureBuilder`, `PodiumPredictionCalibration` i `MonteCarloRaceSimulator`.
3. Symulacja udostępnia pełny rozkład miejsc, zachowując identyczne stare wyniki podium.
4. Oddzielna kalibracja final/Q1 dopasowana na wcześniejszych rundach. Nie jest to drugi model prędkości zawodnika.
5. Suma `P(miejsce) × punkty`, osobno dla finału i Q1, następnie ich suma.
6. Aktualny katalog Pinkbike stanowi pulę kandydatów. Potwierdzona lista startowa, jeśli zostanie dostarczona, dodatkowo ogranicza tę pulę, lecz nie jest wymagana.
7. Wszystkie pary kobiet + wszystkie czwórki mężczyzn; najlepsza para mieszcząca się w pozostałym budżecie wyszukiwana binarnie w prefiksach cenowych.

Optimizer gwarantuje optimum dla **podanej funkcji EV i dostępnych kandydatów**, nie gwarantuje rzeczywistych punktów.
Nie stosuje greedy po value. Brak ceny, mapowania, prognozy lub kontuzja uniemożliwiają automatyczny wybór takiego zawodnika. Pole `injury` z oryginalnych danych Pinkbike jest odczytywane przez importer; niepusta informacja oznacza kontuzję.
`Predicted position` to średnia z kalibrowanego rozkładu warunkowo przy klasyfikacji. Confidence pochodzi z istniejącego modelu i opisuje jakość dowodów, nie szansę wygranej.

Kalibracja v3 miesza rozkład istniejącego silnika ze spójnym modelem bazowym: historyczna częstość nieukończeń wyznacza rozkład liczby sklasyfikowanych, a bazowa kolejność tych zawodników jest wymienna.
Dla pola N: `P(rider na k) = P(Binomial(N, 1-q) >= k) / N`, gdzie q jest historycznym udziałem wyników niepunktujących.
Jeden współczynnik mieszania na kategorię i etap dobierany jest przez log-loss na wcześniejszych obserwacjach, nie ręcznie.
Suma masy każdego ridera wynosi 1, a łączna zajętość dowolnego miejsca nie przekracza 1. Rozkład empiryczny i dobrane współczynniki pozostają w artefakcie audytowym.
Mały zbiór DH i silne osłabienie sygnału indywidualnego przez kalibrację oznaczają, że prognozy należy traktować jako eksperymentalne. Poprawność optimizera nie dowodzi skuteczności sportowej.

## API

Odczyty wymagają dotychczasowego tokena API:

| Metoda | Ścieżka | Wynik |
|---|---|---|
| GET | `/api/v1/fantasy/current` | runda, zawodnicy, skład lub null, diagnostyka, wersje |
| GET | `/api/v1/fantasy/riders` | katalog z cenami i prognozami |
| GET | `/api/v1/fantasy/recommendation` | skład wraz z kontekstem i diagnostyką |
| GET | `/api/v1/fantasy/rules` | pełne oficjalne zasady |
| POST | `/api/admin/fantasy/sync?prepare=true` | pobranie cen i przygotowanie prognoz |
| POST | `/api/admin/fantasy/sync-html` | ręczny import oryginalnej zapisanej strony, `Content-Type: text/html` |
| POST | `/api/admin/fantasy/prepare` | przeliczenie bieżącej prognozy i walk-forward bez pobierania cen |

POST wymaga dotychczasowego **tokenu administratora**. Publiczna strona nie może uruchamiać scrapingu ani symulacji.
`sync-html` jest audytowanym importem materiału z oficjalnej publicznej strony, nie obejściem jej zabezpieczeń.
Nie należy wysyłać przerobionego HTML ani danych rekonstruowanych z domysłów.

## Konfiguracja i przechowywanie

Ustawienia można dodać do istniejącej konfiguracji środowiska:

```json
{
  "Fantasy": {
    "DataDirectory": "App_Data/Fantasy",
    "ScheduledSyncEnabled": true,
    "SyncInterval": "06:00:00",
    "Import": {
      "SnapshotDirectory": "App_Data/Fantasy/prices",
      "TimeoutSeconds": 25,
      "StaleAfterHours": 24,
      "MinimumRiderCount": 6,
      "RiderMappings": {},
      "NameAliases": {}
    },
    "Events": {}
  }
}
```

`RiderMappings`: zewnętrzne ID Pinkbike → wewnętrzne ID ridera.
`NameAliases`: wewnętrzne ID → lista zweryfikowanych alternatywnych nazw.
`Events`: ID rundy → `Q1BonusApplies`, `StartListRiderIds`, `StartListSourceUrl`, `StartListFetchedAtUtc`.
Lista musi zawierać rzeczywiste wewnętrzne ID, źródło HTTPS i czas potwierdzenia. Nie należy wpisywać do niej wszystkich riderów Pinkbike tylko po to, żeby usunąć ostrzeżenie.
W World Cup 2026 domyślnie obowiązuje Q1; inny format wymaga jawnej konfiguracji, a nie zgadywania.

Harmonogram jest włączony w `appsettings.json`: synchronizacja odbywa się w tle po uruchomieniu API i co 6 godzin, aby prognozy nie pozostawały wygasłe bez odświeżenia. Zmiana ustawienia wymaga restartu API.
W środowisku z wieloma instancjami zostaw `ScheduledSyncEnabled` tylko na hoście przeznaczonym do synchronizacji; pozostałym ustaw `false`. Błąd importu nie blokuje niezależnego odświeżenia prognoz.
Nie uruchamiaj wielu instancji synchronizatora nad jednym katalogiem: blokady są lokalne dla procesu.

Snapshoty cen są dopisywane, nie zastępowane. Prognoza jest atomowo zastępowana dopiero po udanym przygotowaniu.
`App_Data/Fantasy` jest ignorowane przez Git; na serwerze wymaga trwałego wolumenu i kopii zapasowej.
Zmiana season/round, konfiguracji startowej, wersji scoringu/modelu/kalibracji lub metadanych wyników unieważnia prognozę.
Metadane (liczba rekordów i najnowsze `updated_at`) sprawdzane są z cache 60 s; korekty powinny aktualizować `updated_at`, tak jak istniejący import.
Przygotowanie odrzuca wynik, jeśli dane zmieniły się w trakcie obliczeń. Cache składu uwzględnia snapshot cen.
Zmiany odczytu nie wykonują drogiego przeliczania: nowe prognozy przygotowuje synchronizator lub administracyjne `prepare`.

## Backtest i ograniczenia

Administracyjne `prepare` zapisuje raport w `App_Data/Fantasy/predictions/<hash-round-id>.json`:
MAE, RMSE, Spearman, top-6 recall, parametry kalibracji i wersje danych.
Raport obejmuje poprzedni i bieżący sezon, z wcześniejszą historią do budowania cech.
Każda prognoza korzysta wyłącznie z danych sprzed początku ocenianej rundy; kalibracja także jest dopasowywana chronologicznie.

Punktacja 2026 jest stosowana retrospektywnie — wyniki raportu nie są oficjalnymi historycznymi season totals.
Brak kompletnego, współczesnego danej rundzie snapshotu cen oznacza brak salary optimization, oracle i regret.
Importer może dziś odczytać historyczne ceny ze strony, ale data ich dzisiejszego pozyskania nie dowodzi, że taki katalog był dostępny przed dawną rundą.
`FantasyBacktester.Evaluate` obsługuje legalny skład i oracle, gdy dostanie rzeczywisty, kompletny snapshot sprzed startu.
Wbudowane przygotowanie łączy kategorie dla historycznego składu tylko przy potwierdzonej przed startem liście, właściwym snapshotcie cen i kompletnych prognozach/wynikach ocenianych kandydatów.
Bez tych danych jawnie wykonuje ocenę samych prognoz.

Lokalnie Pinkbike zwraca HTTP 403 na automatyczny import. Zachowywany jest ostatni dobry snapshot i pokazywana diagnostyka; można użyć ręcznego, oryginalnego zapisu publicznej strony.
Brak potwierdzonej listy startowej nie blokuje rekomendacji. Skład z katalogu Pinkbike jest oznaczony jako oparty na niepotwierdzonym udziale.

Od 17.09.2026 przygotowanie prognoz dla rundy live próbuje również odczytać roster Timed Training z ChronoRace (kontekst rundy) albo listę startową fazy Timed Training / Timed Practice w Tissot (odkrywane numery wydarzeń i faz w danym sezonie). Nie wykorzystuje częściowej listy finiszerów ani listy z finału jako pełnej puli treningowej. Jeśli dostawca nie publikuje treningów, pozostaje katalog Pinkbike. Nazwy są dopasowywane bez uwzględniania wielkości liter, akcentów i kolejności imienia/nazwiska, osobno dla kobiet i mężczyzn; niejednoznaczności nie są zgadywane. Użyte źródło i czas pobrania pozostają w przygotowanym artefakcie. Brak wystarczającego dopasowania nie zeruje całej kategorii.

Roster zawęża wyłącznie wybór Best Team; tabela nadal zawiera wszystkich riderów, w tym kontuzjowanych. Nie usuwa pozostałych konkurentów z modelu wyścigu, ponieważ katalog Pinkbike nie musi obejmować całego pola. Jawnie skonfigurowana lista startowa ma pierwszeństwo. Błąd źródła zachowuje ostatni roster tego samego kontekstu. Runda live ma pierwszeństwo przed kolejną przyszłą rundą. `Fantasy:LiveSyncInterval` (domyślnie 15 minut) określa sprawdzanie rundy i przygotowanie danych live; import cen nadal odbywa się co `SyncInterval`. Publiczne odczyty nie pobierają treningów ani nie uruchamiają symulacji.

Upływ `SyncInterval` sam w sobie nie kasuje zgodnych prognoz: stary czas odświeżenia powoduje ostrzeżenie, natomiast zmiana modelu, reguł, konfiguracji lub rewizji danych nadal unieważnia prognozę. Ostatnie ceny mogą służyć do rekomendacji z ostrzeżeniem o nieaktualności, ale tylko gdy snapshot dotyczy tej samej rundy. Nie podstawia się cen poprzedniej rundy i nie zmienia się daty importu, aby udawać świeżość.
Nie ma automatycznego udawania, że nowa wersja scoringu 2027 jest taka sama jak 2026.

## Weryfikacja

Testy obejmują pełną punktację, statusy i bonusy, sumowanie EV, spójność rozkładów, brak leakage,
dokładność optimizera względem brute force i kontrprzykład greedy, mapowania, prawdziwy zapis formatu Pinkbike,
duplikaty, 403, uszkodzone dane, współbieżne synchronizacje, zachowanie snapshotów,
warunki dopuszczenia składu i kontrakty camelCase we wspólnej konfiguracji HTTP/OpenAPI.
Frontend ma testy schematów, sortowania, wartości brakujących i daty rundy ze strefą czasową.
