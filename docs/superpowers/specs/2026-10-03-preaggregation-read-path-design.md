# Przyspieszenie odczytu preagregacji: Rider Details i Ranking

Status: projekt zatwierdzony przez użytkownika 2026-10-03; warunek: wyniki kalkulacji bez zmian.

## Cel i zakres

Wdrożyć dwa zaakceptowane kierunki optymalizacji:

1. Zapisywać przygotowane składowe sesji potrzebne detalom ridera, zamiast wielokrotnie pobierać i przygotowywać dane jego rywali.
2. Odczytywać pakiety rankingowe jako lekkie rekordy z oddzielnymi metadanymi i ponownie wykorzystywać przygotowanie wspólnych sesji rankingu bieżącego oraz porównawczego.

Nie zmieniać wyników obliczeń, kategorii, filtrów, sortowania, porównania pozycji, publicznych DTO ani wyglądu. Nie wydłużać czasu ważności ogólnego cache API. Frontend pozostaje bez zmian.

Punktem odniesienia jest bieżący kod lokalny, łącznie z wcześniejszą optymalizacją surowego odczytu, podsumowania punktów i parsera pakietów. Nie zastępować go starszym commitem.

## Obecny koszt

Preagregacja rankingowa przechowuje kompaktowe pakiety sesji. Parser nadal rozwija wyniki do zagnieżdżonych obiektów JSON, powielając metadane zawodników, teamów i sesji. Detale ridera pobierają dodatkową populację rywali, aby przygotować skorygowane czasy i statystyki stawki. Samo skrócenie surowego SQL nie usuwa tych kosztów.

Dotychczasowe pomiary wskazują te miejsca jako kandydatów do optymalizacji; nie są gwarancją konkretnego przyspieszenia nowej implementacji.

## Wybrany wariant

Rozszerzyć istniejącą preagregację o przygotowane dane obliczeniowe i dodać typowany odczyt wewnętrzny. Zachować dotychczasową ścieżkę jako bezpieczny fallback.

Alternatywy niewchodzące w zakres:

- Cache gotowych odpowiedzi dla kombinacji filtrów: mnoży warianty, komplikuje unieważnianie i nie przyspiesza pierwszego odczytu.
- Przepisanie kalkulatorów do SQL: duże ryzyko rozbieżności i utrzymywania dwóch implementacji tych samych reguł.

## 1. Przygotowane składowe sesji

### Granica danych

Przygotowanie odbywa się dla pełnej stawki konkretnej sesji i kategorii przy użyciu obecnego kalkulatora C#. Nie zapisujemy ostatecznego Rating/Pace/Consistency dla dowolnego zakresu sezonów. Agregacja wybranego przez użytkownika zakresu pozostaje w API.

Przygotowany pakiet zawiera składowe faktycznie konsumowane przez obecną ścieżkę obliczeń: identyfikatory wyników i riderów, istniejące statystyki sektorów/stawki, referencje czasu po korekcie incydentów oraz usuniętą stratę. Oryginalne czasy wyników i sektorów pozostają niezmienione.

### Zapis i zgodność wersji

Rozszerzyć istniejący rekord preagregacji sesji o osobny przygotowany payload, wersję formatu/algorytmu i identyfikator rewizji źródła. Nie powielać w nim pełnych nazw, historii teamów ani publicznych wyników.

Każde odświeżenie źródłowego pakietu unieważnia przygotowany payload i nadaje nową rewizję. API oblicza przygotowane dane istniejącym kalkulatorem i zapisuje je warunkowo, wyłącznie jeśli rewizja źródła nie zmieniła się w międzyczasie. Przy równoległym imporcie odrzuca przestarzały zapis.

Unieważnianie obejmuje całą sesję, nie tylko zmienionego ridera: zmiana jednego wyniku może zmienić odniesienie całej stawki. Migracja musi pokryć zależności istniejących triggerów, w tym zmiany wyników, splitów, punktów oraz metadanych wpływających na kalkulacje. Bezpośrednie odświeżenie SQL również unieważnia przygotowany payload.

### Odświeżanie

Istniejące ścieżki importu i przeliczenia punktów używają jednego niewielkiego serwisu: odświeżenie pakietu źródłowego, przygotowanie C#, warunkowy zapis. Nie dodawać nowego systemu kolejek ani osobnego harmonogramu.

Obecna operacja odbudowy preagregacji obejmuje również brakujące lub nieaktualne przygotowane payloady. Błąd przygotowania nie usuwa poprawnego pakietu źródłowego i nie blokuje odczytu przez fallback.

### Odczyt Rider Details

Szybka ścieżka zwraca dane wybranego ridera, jego oryginalne wyniki/sektory oraz przygotowane składowe, bez transferowania sektorów całej populacji rywali.

Ważność danych sprawdzać względem rewizji źródła, wersji algorytmu i zakresu stawki. Jeśli filtr zmienia populację stanowiącą podstawę statystyk lub pakiet jest niekompletny, używać istniejącej ścieżki. Nie wykorzystywać częściowo pasujących statystyk. Zwykłe ograniczenie listy sesji nie wymaga ponownego przygotowania niezmienionych sesji.

Zachować dotychczasową semantykę braku kategorii oraz jej rozstrzygania; nie mieszać juniorów i elity. Zapisy tylko rolą serwisową, odczyty z dotychczasowymi zasadami dostępu; nie udostępniać administracyjnego zapisu anonimowo.

## 2. Lekki odczyt pakietów rankingowych

Wprowadzić wewnętrzne niemutowalne rekordy wyników i przygotowanych sesji. Metadane riderów, teamów i sesji przechowywać raz w słownikach; rekordy wyników odwołują się do nich identyfikatorami.

Zachować kolejność pakietów i obecną regułę rozstrzygania powtórzonych metadanych: ostatni rekord wygrywa w całej uporządkowanej sekwencji. Nie zastępować jej niezależnym wyborem nazwy/teamu w każdej sesji. Historia teamów nadal ma dotychczasowe pierwszeństwo.

Typowane dane trafiają bezpośrednio do kalkulatorów i budowania wierszy rankingu. Adapter do starszej reprezentacji pozostaje tylko dla konsumentów, którzy faktycznie jej wymagają; główny ranking nie materializuje pełnego zagnieżdżonego JSON dla wszystkich wyników.

W obrębie obsługi żądania przygotowane dane wspólnej sesji mogą być wykorzystane przez oba rankingi. Klucz obejmuje sesję, kategorię, rewizję, wersję algorytmu i parametry wpływające na populację. Dane muszą być niemutowalne, aby filtrowanie jednego rankingu nie zmieniało drugiego. Nie wprowadzać nowego długowiecznego cache ani zmieniać istniejącego TTL.

Zachować kontrakt wewnętrznego RankingSnapshot wymagany przez overview i pozostałych odbiorców lub zapewnić adapter na ich granicy. Nie duplikować kalkulatorów dla nowej reprezentacji.

## Weryfikacja i wdrożenie

1. Przed zmianami zapisać pełne odpowiedzi bieżącego kodu i wersję danych źródłowych.
2. Dodać testy zgodności parsera, pierwszeństwa metadanych, wspólnych sesji, wersji i unieważniania, w tym wyścigu importu z zapisem przygotowanego pakietu.
3. Porównać pełne odpowiedzi rankingu i overview: cztery kategorie, pojedynczy i wiele sezonów, pełna stawka i wszystkie strony, porównanie rund/filtrów, wyszukiwanie, remisy, filtry statusu, puste dane, brak kategorii i historyczne teamy.
4. Osobno sprawdzić cache przygotowany, brakujący, częściowy i niezgodny wersją; fallback ma dawać identyczny wynik.
5. Uruchomić testy API i izolowany build, nie zatrzymując działającego serwera użytkownika.
6. Zastosować migrację addytywną, odbudować przygotowane dane istniejącą operacją i sprawdzić kompletność. Zachować możliwość wyłączenia nowej ścieżki odczytu bez usuwania danych.
7. Wykonać powtarzane pomiary tego samego zestawu danych: osobno zimny cache procesu przy gotowej preagregacji, ciepły cache i fallback. Raportować medianę, zakres, transfer, czas SQL/HTTP, deserializacji/przygotowania i kalkulacji. Nie przedstawiać zimnego cache procesu jako zimnego cache PostgreSQL.

Warunek akceptacji: brak różnic w wartościach i semantyce publicznych odpowiedzi oraz zmierzone ograniczenie pracy i transferu na ścieżce z preagregacją. Różnica odpowiedzi blokuje włączenie szybkiej ścieżki; nie korygujemy przy okazji reguł biznesowych.

## Poza zakresem

Nowe funkcje UI, zmiany frontendu, strojenie wykrywania incydentów, nowe wzory ocen, rozszerzenie TTL ogólnego cache, nowa infrastruktura oraz publikacja wszystkich istniejących lokalnych zmian.
