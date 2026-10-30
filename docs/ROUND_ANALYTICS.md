# Round Analytics

`GET /api/v1/analytics/round-profile` dostarcza Top Speed vs Run Time, Field Spread, Track Fingerprint i Field Progression. Gdy żądanie wskazuje `roundId`, dodaje sekcję `analytics` z pozostałymi metrykami. Home korzysta z lżejszego zakresu finałowego; szczegóły rundy dobierają również ostatni właściwy pre-final. Frontend rysuje wartości z API, nie powtarza ciężkich obliczeń.

Zakres Analytics pozostaje finałowy niezależnie od oglądanej wcześniej sesji wyników. W interfejsie przejście do Analytics ustawia All i wyłącza wybór sesji; nie oznacza to połączenia dowolnych sesji w jedną sportową populację. Open Men i Open Women pozwalają oglądać łączny widok elite/junior w obrębie rundy; standardowe kalkulatory nadal przygotowują odniesienie w sesji źródłowej. Takich syntetycznych kategorii nie dodajemy do rankingu ani globalnych filtrów.

`Services/AnalyticsService.cs` odpowiada za wybór i pobranie danych, a `Features/Analytics/RoundRaceAnalytics.cs` za obliczenia. Wersja w odpowiedzi to `round-analytics-v2`; wpis `round-analytics` w katalogu fingerprintów ma osobną etykietę `1.0.0`.

## Profil prędkości i rozciągnięcie stawki

Top Speed vs Run Time zestawia oficjalny czas przejazdu z top speed ukończonych finałów. Home pokazuje czołowych 20 riderów, a kolor zwycięzcy i odwrócenie osi czasu są prezentacją. Brak prędkości nie powinien być zastępowany zerem.

Field Spread pokazuje przyrost straty między progami, nie kolejne pełne gapy do lidera. Pierwszy odcinek to Winner → P3. Kolejne progi wynikają z 1/6, 1/3 i 2/3 liczby sklasyfikowanych, zaokrąglanych do najbliższego miejsca z połówką w górę. Progi ≤P3, powtórzenia i pozycje poza polem są odrzucane. Dla 15 finisherów otrzymujemy P3/P5/P10, dla 30 — P3/P5/P10/P20, a dla 60 — P3/P10/P20/P40. Bardzo małe pola mają mniej bloków; nie tworzymy sztucznych rang.

API zachowuje zarówno `gapToLeader`, jak i `previousPosition`, `incrementalSpread` oraz `incrementalSpreadPercent`. Procent przyrostu odnosi się do czasu lidera, nie czasu poprzedniego ridera. Remisy wypełniają pominięte pozycje tym samym czasem, dzięki czemu próg wewnątrz remisu nie tworzy dziury. Osobna klasyfikacja gęstości na podstawie P10 nie zależy od progów wykresu.

Field Progression używa P3, P5 i kolejnych wielokrotności pięciu oraz ostatniej pozycji. Wykres przedstawia różnice między kolejnymi blokami. Tylko dla czytelności może usunąć odstający końcowy ogon: porównuje przyrost na jedną pozycję, wymaga przynajmniej trzech wcześniejszych bloków i przekroczenia połowy pola. Każdy blok usuwanego sufiksu musi przekraczać `max(4 × wcześniejsza mediana, mediana + 6 × MAD, 0.001 s/pozycję)`. Pojedynczy skok wewnątrz stawki ani brak danych nie wystarczają. `positionSpreadFullPosition` zachowuje pełny zasięg do ujawnienia skrócenia; oficjalne wyniki i Field Spread nie są obcinane.

Track Fingerprint porównuje rozrzut ocen sektorowych: dla każdego odcinka bierze różnicę P90−P10 ocen, z co najmniej trzema różnymi riderami. API zwraca rozrzut, skalę względem największego rozrzutu i udział w sumie rozrzutów. Dominujący sektor ma największą separację ocen, niekoniecznie najdłuższy czas lub największą liczbę wypadków. Współdzielone obserwacje pochodzą z istniejącego kalkulatora Pace.

## Sector Efficiency

Metryka szacuje lokalną gęstość stawki w pozycjach na sekundę. Czasy sektora są porządkowane, remisy otrzymują średnią rangę, a wynik jest medianą nachyleń między kolejnymi różnymi czasami. Wymagane są przynajmniej trzy pomiary. `1 s = 5.4 positions` opisuje gęstość tego pola, nie gwarantuje awansu o pięć miejsc dowolnemu zawodnikowi.

## Chaos Index

To heurystyczna skala 0–100 łącząca zmiany rang między kolejnymi checkpointami (40%), udział wykrytych incydentów (20%), DNF (20%) i przetasowanie pre-final/final (20%). Ruch pozycji jest liczony przez znormalizowaną odległość Spearmana typu footrule na wspólnych riderach. Brakujące składowe są pomijane, a pozostałe wagi renormalizowane. DNS odpada z liczby startujących. Bez przynajmniej trzech startujących oraz ruchu checkpointów lub porównania pre-final wynik jest pusty, nie wyprowadzony wyłącznie z DNF.

W `v2` surowy wynik `r` przechodzi dodatkową kalibrację:

```text
contrast = 100 × (1 − exp(−(r/25)^2))
Chaos = round((r + contrast)/2, 1)
```

Składowe pozostają surowymi udziałami; nie są przeskalowywane tą krzywą. Kalibracja zwiększa kontrast prezentacji, nie jest estymacją prawdopodobieństwa wypadku ani potwierdzonym modelem przewidywalności.

## Quali vs Final Correlation

Ostatni dopuszczalny pre-final wybierany jest w tej samej rundzie i kategorii według daty, kolejności sesji i etapu: Quali/Q1/Q2/Semi/Seeding. Dopasowane sklasyfikowane wyniki tworzą scatter plot i korelację rang Spearmana z uśrednieniem remisów. Potrzebne są trzy pary i niezerowa wariancja. Bliskość +1 oznacza podobną kolejność, nie identyczne czasy ani gwarancję przyszłej predykcji.

## Sector Contribution i Run Efficiency

Wkład sektora to czas P2 minus czas zwycięzcy na tym odcinku. Ujemny wkład oznacza stratę zwycięzcy. Rozkład jest zwracany tylko wtedy, gdy komplet sektorów obu zawodników sumuje się dokładnie do końcowej przewagi. Procent może być ujemny lub przekraczać 100%; przy zerowej przewadze procenty pozostają puste.

Run Efficiency opisuje zwycięzcę: liczbę zmierzonych sektorów, wygrane i Top 3 sektory, średnią pozycję sektorową oraz najmniejszą liczbę dodatnich zysków odpowiadającą za 80% sumy **dodatnich** zysków nad P2, przed odjęciem strat. To nie 80% końcowej przewagi netto. Procent wygranych/Top 3 opiera się na zmierzonych sektorach; bez nich jest pusty. Wiele wygranych sektorów nie jest samo w sobie pełną definicją efektywności przejazdu.

## Performance Distribution i braki

The UI displays the histogram and median only; individual outlier rider names and scores are not rendered. Outliers remain in the API contract and the information tooltip reports their count.

Rozkład korzysta z Pace finałowych przejazdów sklasyfikowanych, nie z sezonowego Ratingu. Ma dziesięć przedziałów `[0,10)` do `[90,100]`, medianę i obserwacje poza granicami Tukeya 1,5 IQR przy co najmniej czterech ocenach. Osobno raportuje liczebność ocen i finisherów, ponieważ pokrycie może być niepełne.

Brakujące dane pozostają `null` lub pustą kolekcją. Niepowodzenie pobrania nie jest wynikiem zero. Nie rekonstruujemy sektora z przedziału obejmującego brakujący checkpoint i nie dodajemy mocków. Upset Index nie jest częścią implementacji. Zasady Pace i incydentów są w [ALGORITHMS.md](ALGORITHMS.md).
