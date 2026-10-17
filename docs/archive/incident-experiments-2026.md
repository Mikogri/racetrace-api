# Eksperymenty detekcji incydentów — 2026

> Archiwalne porównanie kandydatów na zamrożonych danych. Nie przedstawia obecnej skuteczności. Reguły produkcyjne: [ALGORITHMS.md](../ALGORITHMS.md).

Stan: 2026-10-01. Badanie offline; **nie wdraża nowego detektora do API**, nie zmienia bazy, oficjalnych wyników ani Pace. Punktem odniesienia jest zapis `current.json` z audytu lokalnego detektora C#.

## Próbka i sposób oceny

- 3486 oficjalnych przejazdów, 9 rund; zamrożone wejścia audytu z 2026-09-30.
- CSV: 109 rekordów, z czego 86 jednoznacznie dopasowanych przejazdów do oceny. Osobno 4 DNS/DSQ i 19 treningów lub rekordów bez określonej sesji.
- CSV zawiera potwierdzone incydenty, ale nie kompletny wykaz incydentów ani potwierdzone przejazdy bez incydentów. Można mierzyć pokrycie listy; **nie można obliczyć precision, false-positive rate ani prawdopodobieństwa incydentu**.
- Trafienie oznacza wskazanie przynajmniej jednego sektora w opisanym przejeździe. Nie potwierdza prawidłowej lokalizacji; numery stref splitów w CSV nie są dokładnymi etykietami sektorów.
- 2592 konfiguracje: 9 rodzin × 4 minimalne straty czasowe × 8 progów odchylenia × 3 reguły powrotu tempa × 3 wagi pozycji.
- Porównanie przy limitach udziału alarmów: 5%, 10%, 10,3557% (361/3486, poziom obecnego detektora), 12,5%, 15%, 20%. Limit jest jawną polityką obciążenia ręcznej weryfikacji, nie estymacją błędów.
- Test przenoszenia progów: jedna cała runda poza doborem parametrów, dobór na pozostałych, następnie ocena odłożonej rundy. Kategorie i sesje danej rundy są razem. Ponieważ cała lista była wcześniej używana do rozwoju modeli, nie jest to niezależny, nietknięty test generalizacji.

## Podejścia

| Rodzina | Oczekiwany czas / skala anomalii |
|---|---|
| `profile` | Mediana względnego tempa na pozostałych sektorach; rozrzut ridera i stawki |
| `seconds` | Ten sam profil; odchylenie liczone w sekundach, bez sztywnego minimum 10% straty |
| `local` | Mediana tempa na trzech najbliższych innych sektorach; rozrzut ridera i stawki |
| `lower-profile` | Kwantyl 35% pozostałych sektorów, mniej podatny na kilka strat w przejeździe |
| `self-profile` / `self-seconds` | Rozrzut własnego przejazdu bez poszerzania tolerancji przez rozrzut stawki |
| `cohort7` / `cohort15` | Profil 7 lub 15 podobnych riderów z tej samej sesji/kategorii, wybieranych bez badanego sektora |
| `rank-conditioned` | Czas odpowiadający typowemu percentylowi pozycji ridera na innych sektorach; nadal wymaga straty czasu |

Żaden model nie używa nazwisk, CSV ani finalnej pozycji jako reguły detekcji. Aliasy nazwisk służą wyłącznie dopasowaniu etykiet audytu. Wszystkie warianty współdzielą zapisane flagi niewiarygodnego pomiaru istniejącego detektora; nie jest to eksperyment nad czyszczeniem surowych splitów.

## Wyniki

| Wariant | Trafienia CSV | Pokrycie | Wszystkie alarmy | Trafienia przy odkładaniu rund |
|---|---:|---:|---:|---:|
| Obecny C# | 48/86 | 55,8% | 361 | — |
| Lokalny profil, limit alarmów jak obecnie | 53/86 | 61,6% | 349 | 53/86 |
| Lokalny profil, limit 12,5% | 57/86 | 66,3% | 434 | 55/86 |
| Lokalny profil, limit 15% | 60/86 | 69,8% | 511 | 59/86 |
| Lokalny profil, limit 20% | 67/86 | 77,9% | 687 | 66/86 |
| Maksimum pokrycia w siatce, bez limitu alarmów (`cohort15`) | 72/86 | 83,7% | 955 | nie wybierano w tym trybie |

Najlepszy badany kompromis to **lokalny profil tempa**, a nie samo poluzowanie procentowego progu ani model oparty głównie na pozycji. Porównanie z sąsiadami jest bardziej kosztowne i przy tych samych limitach alarmów nie poprawia wyniku. Duża czułość nie dowodzi poprawności dodatkowych alarmów.

### Kandydat do dalszej weryfikacji: limit 12,5%

`local; loss >= 1.5 s; z >= 1.5; z + 2 × max(0, pogorszenie percentyla pozycji) >= 3.5; co najmniej 2 zgodne sektory odniesienia`.

Oczekiwany czas = mediana czasu sektora w stawce × względne tempo ridera na trzech najbliższych innych sektorach. Odchylenie `z` jest skalowane maksimum z rozrzutu tych sektorów, rozrzutu sektora w stawce i dolnego progu 0,02 w skali logarytmicznej. Badany sektor nie ustala własnego oczekiwanego czasu. Pozycja wzmacnia sygnał, ale nie zastępuje straty sekundowej i odchylenia czasowego.

- 10 dodatkowych trafień z listy i 1 utracone względem C#: Gracey Hemstreet, Les Gets. Nie należy przedstawiać tego jako zachowania wszystkich wcześniejszych trafień.
- 29 niewykrytych przejazdów: 23 ukończone i 6 DNF. DNF sam w sobie nie wskazuje sektora incydentu; brak następnego pomiaru nie uzasadnia wymyślenia czasu.
- Podział na wykrycie i korektę jest konieczny: 434 alarmy, ale tylko 373 przejazdy spełniają badawcze bramki rekonstrukcji czasu, w tym 46 opisanych w CSV. To **nie jest** wynik ponownego przeliczenia produkcyjnego Pace.
- Bramki rekonstrukcji: ukończony sklasyfikowany przejazd, komplet wiarygodnych sektorów, zgodność sumy z metą do 0,01 s, co najmniej połowa sektorów i minimum dwa bez flagi, powrót do profilu po stracie albo pojedynczy końcowy sektor. Ciągłego spowolnienia nie korygujemy automatycznie.

## Pierron

| Finał | Wykrycie | Czas sektora | Szacowana strata | Oficjalna meta | Badawcza meta po korekcie |
|---|---|---:|---:|---:|---:|
| Mona Yongpyong | S4 | 41,601 s | 2,532 s | 165,364 s | 162,832 s |
| Loudenvielle | S2 | 63,508 s | 4,829 s | 216,258 s | 211,429 s |

Korekta usuwa nadwyżkę względem **własnego przewidywanego tempa**, nie cały gap do zwycięzcy. W Loudenvielle nie odejmuje więc automatycznie 5,928 s. Rzeczywista strata wskutek upadku nie jest mierzalna z samych splitów; 4,829 s jest estymacją modelu, nie potwierdzonym pomiarem. Jordan Williams w pierwszej rundzie pozostaje niewykryty: jeden szybki sektor, potem utrzymujące się spowolnienie, za mało niezależnego czystego tempa.

## Odtworzenie

Z katalogu głównego repozytorium API:

```powershell
python tools/incident-experiments.py --input artifacts/incident-audit/inputs-2026.json --baseline artifacts/incident-audit/current.json --labels tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv --output artifacts/incident-experiments
```

Python, tylko biblioteka standardowa. Każde uruchomienie wykonuje kontrole: równy wolny/szybki przejazd, drobna strata, samotna strata 5 s, brak pomiarów, spadek pozycji bez istotnej straty, stabilna grupa porównawcza oraz zakaz rekonstrukcji DNF, błędnego pomiaru i straty bez powrotu tempa.

Pliki wynikowe (lokalne, ignorowane przez Git):

- `report.md`: wszystkie konfiguracje wybrane przy limitach i każdy rekord CSV;
- `summary.json`: parametry, wyniki odłożonych rund i hashe wejść;
- `grid.csv`: wyniki wszystkich 2592 konfiguracji;
- `candidates.csv`: wskazania kandydata 12,5%, sektor, przewidywany czas i strata;
- `missed.csv`: każdy niewykryty rekord, czasy i pozycje sektorowe oraz najsilniejszy sygnał.

SHA-256 wejść:

```text
inputs-2026.json  566df243df09d8ba3d6d34f9accd6cf1fe74f45d74e05c2bc8ff40e804ca275e
incidents-2026.csv 42afdb3bbc1678987cc9c1e473ceb731390846a80ef562cfdb4e21babff2c8a1
current.json      fa7b2a58ffd043d31dda603feaaf8b2582991a73d5614237cd044b94a6d4760f
```

## Decyzja

Nie promowano żadnego wariantu do API. Najpierw ręczna weryfikacja nowych wskazań oraz próbki przejazdów bez incydentów, osobno według kategorii/rundy. Próg do korekty Pace powinien być bardziej konserwatywny niż próg do pokazania podejrzenia incydentu. Nie ma podstaw do uznania 955 alarmów za poprawę tylko dlatego, że obejmują 72 znane przypadki.
