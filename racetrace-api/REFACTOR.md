# Race Trace — refactor zachowujący zachowanie

## Zakres i granice

Zmiany dotyczą struktury kodu API i wspólnej serializacji filtrów w React.
Nie zmieniono kontrolerów, routingu, SQL, konfiguracji produkcyjnej, CSS, JSX,
kluczy cache, polityk odświeżania, algorytmów Pace/Consistency ani kolejności I/O.
Zastany lokalny stan kalkulatora Pace i zmiany kolorów frontendu nie były cofane
ani edytowane w ramach tej pracy. Nie wykonywano importów ani zapisów do bazy.

## Najważniejsze zmiany

- `Infrastructure/Supabase/CompactMetricsPackageParser.cs`: dekodowanie pakietów
  RPC wydzielone z DomainQueryService; zachowano indeksy kolumn, starsze formaty,
  klonowanie JSON, wartości domyślne i przygotowanie typowanych danych Pace.
- `Features/Metrics/MetricsQueryParser.cs`: jeden parser HTTP → RiderMetricsContext.
  Serwisy rankingu, riderów, compare i pozostałe wywołują go bez zależności od
  statycznego parsera w serwisie dostępu do bazy. Stary punkt wejścia deleguje
  do niego dla kompatybilności.
- `Features/Results/ResultTimingProjection.cs`: obliczanie sektorów, pozycji,
  czasów referencyjnych i Perfect Run z już pobranych danych, bez I/O.
- `Features/Results/SessionResultMapper.cs`: mapowanie publicznych wyników
  i wspólne konwersje jednostek. ResultsService zachowuje orkiestrację odczytów.
- `Features/Riders/RiderTimingStatistics.cs`: przygotowanie sektorów i zliczanie
  wygranych sektorów/splitów oddzielone od orkiestracji detali.
- `Features/Riders/RiderTeamHistoryMapper.cs`: osobne mapowanie dwóch istniejących
  formatów historii teamów, bez ich sztucznego ujednolicania.
- `Features/Imports/ChronoRace/ChronoRacePayloadMapper.cs`: normalizacja i tworzenie
  payloadów oddzielone od zapisu. Batchowanie, kolejność operacji, fallbacki,
  usuwanie starych wyników oraz aktualizacje danych pochodnych pozostały bez zmian.
- Publiczne DTO rozdzielono między katalogi Ranking, Riders, Results i Rounds.
  Zachowano nazwy typów, namespace Models i wszystkie atrybuty serializacji.
- `Infrastructure/ServiceCollectionExtensions.cs`: konfiguracja, HTTP/cache,
  Supabase oraz usługi aplikacji rejestrowane w oddzielnych metodach.
  Program.cs pozostaje kompozycją aplikacji i dotychczasowego middleware pipeline.
- Frontend `src/utils/metrics-api-query.ts`: wspólne przygotowanie parametrów
  rankingu, overview i pressure-performance; wspólna serializacja CSV również
  dla compare. Compare zachowuje swoje dotychczasowe aliasy parametrów.

## Semantyka zachowana celowo

- Puste i nieobecne listy filtrów nadal oznaczają brak ograniczenia po stronie API.
  Nie dodano nowego znaczenia pustego wyboru. Zachowano kolejność aliasów i wartości.
- Ranking frontendu nadal domyślnie wybiera elite_men; helper nie narzuca tego
  pozostałym endpointom. Query keys i zachowanie loading nie są zmieniane.
- Czas przechowywany i payload importu: milisekundy; silnik Pace: jawnie nazwane
  sekundy; publiczne ResultTimeDto: sekundy. Nie wykonano globalnej zmiany jednostek.
- `sessionResults` i `teamHistory` overview nadal zachowują istniejący JSON.
  Test pokazuje m.in. stringowy identyfikator obok liczbowego oraz dodatkowe pola
  teamu. Zastąpienie ich naiwnie rekordem z long/DateOnly mogłoby zmienić kontrakt.
  Pełna migracja surowych danych do typowanych modeli NIE jest zakończona.
- Kalkulatory metryk już przyjmują typowane dane bez HTTP/Supabase/JSON; nie było
  potrzeby zmieniania matematyki ani dokładania wersjonowania algorytmu.
- Nie wprowadzono fail-fast dla brakujących sekretów/URL: dotychczas aplikacja
  może wystartować bez nich, a konkretne endpointy zwracają kontrolowany błąd.
  Bezwarunkowe ValidateOnStart zmieniłoby to zachowanie. Opcjonalny seed już
  sprawdza istnienie pliku przy starcie, jeżeli został skonfigurowany.
- Globalny format błędów i middleware pozostawiono bez zmian. ProblemDetails
  nie został narzucony istniejącemu frontendowi.

## Testy i sprawdzenia

Stan początkowy: frontend 64/64; API 434/437. Trzy testy schematu SQL zależały
od lokalizacji bin/Debug i nie działały z osobnym katalogiem artefaktów.
SQL jest teraz kopiowany jako fixture testów; nie zmieniono jego treści ani asercji.

Dodano 9 testów API: puste filtry, aliasy i kolejność, jednostki i kontrakt wyniku,
priorytet konfiguracji sekretów, lifetime usług, dwa formaty historii teamów.
Dodano 3 testy frontendu: aliasy parametrów, brak ukrytych defaults, kolejność CSV.

Weryfikacja: API 446/446; frontend 67/67. Solution build, frontend lint,
typecheck i production build przechodzą. Testy API uruchamiano z
`dotnet test ../racetrace-api.slnx --artifacts-path <katalog-tymczasowy>`
bez zatrzymywania działającego lokalnego API.

Nie wykonano pełnego porównania wszystkich endpointów z żywą bazą ani wizualnych
testów przeglądarkowych. Zgodność zabezpieczają istniejące i nowe testy regresji
oraz brak zmian w warstwie wizualnej i logice requestów.

## Pozostałe uwagi

Build zgłasza istniejące NU1903 dla zależności Microsoft.OpenApi 2.0.0.
Aktualizacja zależności bezpieczeństwa powinna być osobną, zweryfikowaną zmianą.
Nie stwierdzono i nie poprawiano błędu biznesowego podczas tej refaktoryzacji.
Nie jest to pełny audyt poprawności danych produkcyjnych.

Wydzielenie mapperów i parserów jest inkrementalne: pozostałe legacy odczyty
i surowy JSON nie zostały przepisane masowo. Kolejne ekstrakcje powinny zachować
te same kontrakty i być zabezpieczane testami, nie globalną zmianą modeli.
