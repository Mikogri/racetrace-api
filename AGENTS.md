## Formatting

Use the repository-pinned CSharpier with its default rules for all C# changes. Run `dotnet tool restore`, `dotnet csharpier format .`, then `dotnet csharpier check .` from this repository. Do not hand-format code, use a competing formatter, or bypass syntax validation. Respect `.csharpierignore`: generated files, build artifacts and existing database migrations must remain untouched. VS Code uses the recommended CSharpier extension on save. Preserve unrelated local edits.

## Dokumentacja

Przy zmianach w kodzie aktualizuj odpowiednią dokumentację, jeśli zmiana wpływa na architekturę, kontrakty, przepływ danych, algorytmy lub zachowanie systemu. Dokumentacja musi opisywać aktualną implementację, a nie historyczne założenia. Nie twórz nowego pliku dokumentacji, jeśli informację można dopisać do istniejącego.

Główna dokumentacja znajduje się w `docs/`; punktem wejścia jest `README.md`. Materiały w `docs/archive/` opisują historyczne pomiary i eksperymenty, nie bieżące działanie aplikacji.
