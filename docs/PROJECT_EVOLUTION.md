# RaceTrace — historia zmian technicznych

Stan: 17.09.2026. Zakres: większe zmiany odtworzone z dostępnej historii czatu, kodu i dokumentacji projektu. Pominięto kosmetykę oraz niepotwierdzone operacje.

## Architektura

- Frontend React/TypeScript, Vite i TanStack Query; API .NET; dane historyczne w Supabase.
- Aplikacja Blazor była punktem odniesienia dla zakresu danych podczas budowy nowego frontendu.
- Obliczenia domenowe i przygotowanie statystyk przenoszono do API, ograniczając ich duplikowanie w interfejsie.
- Refaktoryzacje wydzielały współdzielone komponenty i usługi bez zamierzonej zmiany kontraktów, metryk ani zachowania aplikacji. Poprawność zabezpieczano testami regresji.

## Ranking

- Jedna tabela z filtrami i sortowaniem kolumn zastąpiła oddzielne tryby Points / Standings / Rating. Domyślne sortowanie: Rating.
- Paginacja API ogranicza transfer; podstawowy rozmiar strony wynosi 30 rekordów. Kontrolka pokazuje ograniczony zestaw numerów stron.
- Preagregacja danych sesji i ograniczenie transferu rankingowego zmniejszają koszt odczytu.
- Dodano zmianę pozycji ridera. Zrezygnowano z równoległego pokazywania zmian każdej metryki.
- Domyślny ranking porównywany jest ze stanem przed ostatnią rundą; po zmianie filtrów — z poprzednim kontekstem filtrów.
- Wejście linkiem z filtrami korzysta z domyślnego kontekstu tej samej kategorii. Jeśli konteksty są równoważne, odniesieniem pozostaje poprzednia runda.
- Sortowanie nie zastępuje bazy porównania. Dla sortowania po nazwie zmiany pozycji są ukryte. Brak wspólnego ridera oznacza brak wskaźnika, a nie fikcyjny awans.

## Wyniki rund i pomiary czasu

- Results zawiera kalendarz sezonu w formie kart, bez paginacji. Sezony pochodzą z API; nieistniejący sezon z URL nie zaznacza innego roku.
- Szczegóły rundy umożliwiają wybór kategorii, sesji i widoku sektorów lub splitów, sortowanie pomiarów oraz przejście do ridera.
- Split oznacza czas narastający, sektor — czas odcinka. Cztery splity odpowiadają pięciu sektorom, łącznie z dojazdem do mety.
- Perfect Run jest osobnym rekordem API: bez ridera, wyniku i pozycji, poza paginacją. Czas końcowy jest sumą najlepszych sektorów.
- Prezentacja rozróżnia lidera sesji i najlepszy czas odcinka. Lider pokazuje czasy; pozostali riderzy oraz Perfect Run mogą pokazywać różnice do lidera. Lider nie musi być najszybszy na każdym sektorze.
- DNF/DNS/DSQ zastępują brakujący końcowy czas. Winner wynika z rzeczywistego wyniku sesji, nie z kolejności po sortowaniu.
- Runda live prowadzi do Live Timing, nie do zwykłych szczegółów archiwalnych.

## Profile riderów i model danych

- Detale mają wspólne filtry i zakładki Stats, Results oraz Tracks. Przełączanie zakładek nie resetuje filtrów.
- Pre-Final obejmuje wszystkie etapy przed finałem, nie tylko kwalifikacje. Historia startów pokazuje przebieg kolejnych sesji.
- Results i Tracks mają paginację po 15 rekordów. Tracks agreguje metryki według tras; Results umożliwia przejście do rundy z kontekstem kategorii.
- Zbiorczy endpoint overview dostarcza dane profilu i wykresów, ograniczając potrzebę odpytywania starszych endpointów osobno.
- Dodano opcjonalne gender. Źródłem płci jest API, nie heurystyka frontendu.
- Wspólny katalog kategorii zachowuje podstawową kolejność i obsługuje dodatkowe kategorie wykrywane w danych. Opcje filtrów są odświeżane z API.
- Historia teamów opisuje okresy przypisania. Team zapisany przy wyniku ma pierwszeństwo; przy jego braku używane jest przypisanie z dnia sesji.
- Ranking dobiera team do ostatniego wyniku w analizowanym zakresie. Import bez teamów nie zeruje historii.

## Metryki i wykresy

- Pace opisuje tempo, Consistency powtarzalność, Rating łączy ocenę sportową, a Confidence opisuje jakość dowodów. Confidence oddzielono od oceny osiągów.
- Obliczenia izolują kategorie; uwzględniają także incydenty i nieukończone przejazdy.
- Wykresy przeniesiono do Recharts. Wspólne zasady obejmują tooltipy, formatowanie, puste serie i reakcję na odświeżenie.
- W profilu wykres punktów przedstawia sumę narastającą, a wykres Rating — wartości kolejnych rund. Zastąpiły wcześniejsze osobne przebiegi Pre-Final i Final.
- Wykres wyników pokazuje rzeczywiste pozycje z marginesem osi, zamiast ograniczać wszystkie słabsze wyniki do 60. miejsca.
- Pace / Consistency może zawężać zakres osi do danych z marginesem. Skrajne punkty nie powinny być przycinane.
- Naprawiano dopasowanie sesji między punktem wykresu, tooltipem i tabelą. Kolor wartości progowej odpowiada wartości wyświetlanej po zaokrągleniu.

## Home

- Dodano przegląd sezonu: Next Round, Last Round Results, Season Progress, Podium Predictor, Top Rated Riders i Current Standings.
- Kategorie poszczególnych sekcji są niezależne. Ich zmiana nie przewija strony na górę.
- Dodano wykres Top Speed względem końcowego czasu przejazdu dla pierwszych 20 riderów finału ostatniej rundy, z wyborem kategorii i przejściem do wyników.
- Sekcje zachowują strukturę podczas ładowania i błędów; brak danych nie jest zastępowany fikcyjnymi rekordami.

## Compare

- Porównanie obejmuje dwóch riderów we wspólnej kategorii i zakresie danych. Wybrani riderzy są zapisani w URL.
- Modal wyboru korzysta z wyszukiwania, paginacji i sortowania po punktach.
- Widok zestawia statystyki, sektory, wyniki, Pace / Consistency, skumulowane punkty i Rating.
- Stałe kolory identyfikują riderów w całym porównaniu. Tooltip pokazuje wielu riderów pod wspólnym kontekstem rundy i sesji.
- Wspólna oś porównania nie może przesuwać danych wskutek brakującej sesji zawodnika.

## Live Timing

- Własne API dostarcza kontekst rundy; bieżące wyniki pochodzą z ChronoRace lub Tissot Timing. Nie odtwarza się transmisji przez ciężkie zapytania analityczne.
- Obsługa Tissot odkrywa wydarzenia i fazy na podstawie sezonu, bez założenia stałych numerów eventów.
- Lista sesji rozróżnia kategorię i etap zawodów. Finały są prezentowane przed kwalifikacjami.
- Tabela łączy wyniki i listę startową, usuwa duplikaty, pokazuje godziny startu oraz rozdziela riderów oczekujących, na trasie i zakończonych.
- DNS/DSQ nie są traktowane jako upcoming. Brak czasu mety nie generuje końcowego gapu zawodnika na trasie.
- Zrezygnowano z dodatkowego pobierania teamów na potrzeby Live; prezentowany jest kraj ridera.
- On Track zestawia gap do lidera na splitach i mecie. Lider pozostaje widoczny; przy braku riderów na trasie wyświetlane jest podium. Linie identyfikuje legenda kolorów.
- Automatyczne aktualizacje zachowują istniejącą tabelę i nie uruchamiają animacji całej zawartości przy każdym odczycie.

## Wspólne zachowanie interfejsu

- Rozdzielono ładowanie strony, pierwszy odczyt danych i aktualizację istniejącego zestawu: skeleton, spinner oraz progress bar z zachowaniem treści.
- Przy aktualizacji stosowane jest wspólne przyciemnienie i płynny powrót do pełnej widoczności. Live pozostaje stabilny podczas cyklicznych odczytów.
- Ujednolicono Failed to load, No data, Retry, tooltipy, modale, filtry, search boxy i tabele.
- Filtry wielokrotnego wyboru mogą być stosowane po zamknięciu menu, zamiast wysyłać żądanie po każdym zaznaczeniu.
- Nawigacja zachowuje kontekst sekcji w ścieżkach szczegółów. Back nie przechodzi przez historię zakładek profilu.
- Request details pokazuje czas i skalę odpowiedzi. W rankingu pozostawiono implementację ukrytą przełącznikiem.

## Podium Predictor — badania

- Porównano sześć modeli na 40 wyścigach kategorii EM/EW z lat 2022–2024, korzystając z wcześniejszej historii od 2021 r.
- Z prognozy wyłączano cały oceniany weekend. Modele wybierano na sezonach 2022–2023, a sprawdzano osobno na 2024.
- Warianty obejmowały Rating, Pace, czyste tempo, zmienność i ryzyko incydentu/DNF.
- Lepsza kalibracja prawdopodobieństw nie przełożyła się na jednoznaczną poprawę trafiania podium. Produkcyjnego predictora nie zastąpiono; eksperymenty pozostały materiałem do dalszych testów.

## Fantasy

- Dodano katalog riderów, prognozy punktów, Best Team, zasady i diagnostykę danych. Interfejs korzysta ze wspólnych tabel i modali.
- Importer odczytuje katalog Pinkbike: ceny, punkty, identyfikatory i kontuzje. Mapuje zawodników na istniejącą bazę bez zgadywania niejednoznacznych nazw.
- Punktacja 2026 obejmuje osobne tabele kobiet i mężczyzn oraz bonus Q1. Skład: czterech mężczyzn, dwie kobiety, budżet 1,5 mln USD.
- Predicted Points to wartość oczekiwana z rozkładu wyników. Value przedstawia prognozowane punkty względem ceny.
- Dokładny optimizer maksymalizuje sumę oczekiwanych punktów w legalnym składzie; nie wybiera zachłannie po Value.
- Prognozy wykorzystują istniejący silnik wyścigu. Droższe obliczenia są przygotowywane w tle, poza zwykłym odczytem strony.
- Snapshoty cen i prognoz przetrwają restart. Nieudany import nie zastępuje ostatnich poprawnych danych.
- Brak potwierdzonej listy startowej nie blokuje rekomendacji. Kontuzjowani pozostają w tabeli, ale są wykluczeni z automatycznego Best Team.
- Poprawiono wygasanie: przekroczenie czasu odświeżenia nie usuwa zgodnych prognoz. Zmiana danych, modelu lub reguł nadal je unieważnia. Starsze ceny mogą być użyte z ostrzeżeniem tylko dla tej samej rundy.
- Dla rundy live przygotowanie może odczytać roster Timed Training z ChronoRace lub listę startową odpowiedniej fazy Tissot. Brak źródła nie blokuje składu; częściowe wyniki finiszerów nie są pełną listą uczestników.
- Dopasowanie treningów uwzględnia kategorię oraz znormalizowane imię i nazwisko. Roster ogranicza wybór składu, nie usuwa konkurentów z modelu wyścigu. Źródło i czas odczytu są zachowywane.
- Odczyty publiczne nie uruchamiają importu ani symulacji. Operacje administracyjne są chronione osobno.
- Testy obejmują scoring, optimum składu, mapowanie, brak przecieku przyszłych danych, trwałość snapshotów i kontrakty API. Bez historycznych cen nie deklaruje się skuteczności historycznej optymalizacji budżetu.

## Wycofane eksperymenty

- Rider Similarity na Home: dendrogram i późniejszą prezentację grup usunięto.
- Dane demonstracyjne służyły testom interfejsu; przywrócono zasilanie API.
- Eksperymentalny motyw panelowy i Source Sans 3 wycofano do zatwierdzonego frontendu. Zmiany zachowano w Git stash.
- Alternatywne animacje tabel nie są odrębnymi funkcjami produktu.

## Dokumentacja szczegółowa

- [Fantasy](fantasy.md)
- [Test historyczny Fantasy](fantasy-backtest-2026-09-16.md)

Raporty testów opisują stan konkretnego przebiegu. Aktualne reguły działania Fantasy, w tym brak obowiązkowej listy startowej, opisuje dokumentacja modułu.
