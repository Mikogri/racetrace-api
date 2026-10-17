# Konserwatywny detektor — korekta eksperymentu 2026-10-01

> Archiwum etapu badań sprzed promocji detektora. Poniższe „tylko eksperyment” opisuje tamten etap; później wariant wdrożono. Aktualne reguły: [ALGORITHMS.md](../ALGORITHMS.md).

Status: tylko eksperyment offline, bez zmian API, bazy i kalkulatora Pace. Skrypt: `tools/incident-conservative-refinement.py`.

## Reguła

Oczekiwane czasy i normalizacja pozostają z poprzedniego modelu `local`. Wprowadzono dwie niezależne ścieżki, wspólne dla wszystkich riderów:

1. **Duża strata:** co najmniej 4 s nadwyżki, `z >= 1.5`, `z + max(0, rank_drop) >= 4`, minimum dwa zgodne sektory odniesienia. Sama strata 4 s nie wystarcza.
2. **Izolowana strata przy czołowym tempie:** minimum 2 s, `z >= 2`, pogorszenie percentyla pozycji o minimum 0.5, minimum trzy zgodne sektory odniesienia i powrót do tempa. Wymagane rzeczywiste, wiarygodne pomiary bezpośrednio przed i po podejrzanym sektorze. Mediana pozycji na innych sektorach musi mieścić się w najlepszych 15% stawki, a obie sąsiednie pozycje w najlepszych 20%. Minimum sześciu zawodników w polu odniesienia.

Drugiej ścieżki nie stosuje się do pierwszego i ostatniego sektora: nie można potwierdzić tempa z obu stron. To celowy koszt konserwatywności, nie dowód braku incydentu na końcu trasy. Niewiarygodne sektory nie uczestniczą w rankowym potwierdzeniu.

Pierron w pierwszej rundzie ma pozycje sektorowe `2, 2, 1, 23, 1`, a Max Alran w Les Gets Q1 `1, 1, 71, 1, 8`. Oba przejazdy spełniają drugą ścieżkę. Nathan w Lenzerheide ma `3, 4, 12, 15, 29`, Luca w Mona Q1 `6, 5, 13, 18, 64`, Kolb w Leogang `6, 5, 3, 18, 28`: niewielka strata końcowa bez pomiaru potwierdzającego powrót nie wystarcza.

## Ten sam test

Zamrożone 3486 oficjalnych przejazdów sezonu 2026, 86 dopasowanych przejazdów z dodatniego CSV. SHA-256 wejścia: `566df243df09d8ba3d6d34f9accd6cf1fe74f45d74e05c2bc8ff40e804ca275e`.

| Pomiar | Poprzedni konserwatywny | Nowy konserwatywny |
|---|---:|---:|
| Wszystkie oznaczone przejazdy | 349 | 347 |
| Trafienia przejazdów z CSV | 53/86 | 54/86 |
| Oznaczone przejazdy podanych 19 riderów, EM | 36 | 35 |
| Oznaczone sektory tych 19 riderów | 41 | 39 |

W całej próbce: 8 nowych oznaczonych przejazdów i 10 usuniętych. Dodatkowo usunięto drobne sektory z czterech przejazdów, które nadal mają inne oznaczone sektory. Nie interpretować alarmów spoza CSV jako fałszywych trafień.

## Wszystkie zmiany dla podanych 19 riderów

| Rider | Runda / sesja | Przed | Po |
|---|---|---|---|
| Amaury Pierron | Mona Yongpyong / Final | brak | S4: 2.53 s |
| Max Alran | Les Gets / Quali 1 | brak | S3: 8.03 s |
| Finn Iles | Leogang / Quali 1 | brak | S2: 9.26 s |
| Finn Iles | Mona Yongpyong / Quali 1 | S5: 2.29 s | brak |
| Andreas Kolb | Leogang / Final | S5: 3.11 s | brak |
| Andreas Kolb | Lenzerheide / Quali 2 | S4: 62.18 s; S5: 3.13 s | tylko S4: 62.18 s |
| Luca Shaw | Mona Yongpyong / Quali 1 | S5: 1.87 s | brak |
| Nathan Pontvianne | Lenzerheide / Final | S5: 1.64 s | brak |

Pozostałe wskazania pozostają takie same. W szczególności Pierron Loudenvielle Final nadal ma S2: 4.83 s. Czasy są estymacją nadmiernej straty względem własnego tempa, nie rzeczywistym pomiarem straty w wypadku.

## Koszty i ograniczenia

- Zysk z CSV: Pierron Mona Final, Finn Iles Leogang Q1, Max Alran Les Gets Q1.
- Utracone trafienia CSV: Nathan Pontvianne Lenzerheide Final (1.64 s, zgodnie z uwagą użytkownika) oraz Myriam Nicole Lenzerheide Final (S1: 3.69 s). Przypadek Myriam jest znanym kosztem podniesienia progu i braku wcześniejszego sektora; nie należy go ukrywać ani zmieniać jego etykiety w CSV.
- Pięć przykładów użytkownika było użyte do zaprojektowania reguł. Ich zaliczenie jest testem akceptacji zmian, nie niezależną walidacją skuteczności. Nie wyznaczono nowego wyniku cross-validation ani precision.
- Odrzucenie małej straty oznacza brak wystarczających podstaw do silnej flagi analitycznej, a nie potwierdzony brak wypadku.
- Brak zmian oryginalnego CSV. Nazwiska i identyfikatory występują tylko w testach i wyborze raportowanej grupy, nigdy w funkcji detekcji.
- Skrypt ma kontrole syntetyczne: sama zmiana pozycji, mała strata, izolowana strata 2.5/8 s, brak wiarygodnego sąsiedniego sektora i brak czołowego tempa. Osobne sprawdzenia zapewniają pięć zachowań zgłoszonych przez użytkownika.

## Uruchomienie i pełny raport

Z katalogu głównego repozytorium API:

```powershell
python tools/incident-conservative-refinement.py
```

Wyniki lokalne w `artifacts/incident-conservative-refinement/`: `report.md` zawiera pełną listę top 19 przed/po, `all-changes.csv` wszystkie zmiany w sezonie, `summary.json` liczniki i hash danych. Skrypt nie wykonuje requestów ani zapisów do bazy.
