using System.Globalization;
using System.Text;
using Microsoft.VisualBasic.FileIO;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.PodiumBacktest;

// Labelled positives only: unmatched records are explicit, unlabelled runs are NOT negatives.
internal static class ConfirmedIncidentAudit
{
    internal static async Task WriteAsync(string path, RiderDetailDataBundle bundle,
        IReadOnlyDictionary<(string Session, string Rider), RiderSessionPaceResult> scores, string output,
        IReadOnlyDictionary<(string Session, string Rider), RiderSessionPaceResult>? baseline = null)
    {
        using var csv = new TextFieldParser(path, Encoding.UTF8) { HasFieldsEnclosedInQuotes = true };
        csv.SetDelimiters(",");
        var headers = csv.ReadFields()!;
        var sessions = bundle.Data.Sessions.ToDictionary(s => s.String("id")!);
        var names = bundle.Data.Results.GroupBy(r => r.String("rider_id")!)
            .ToDictionary(g => g.Key, g => g.First().Object("riders")!.DisplayName());
        var report = new StringBuilder("# Walidacja potwierdzonych incydentów 2026\n\n");
        report.AppendLine("Etykiety pochodzą z dostarczonego CSV. Brak wpisu nie oznacza czystego przejazdu. Nie wyliczamy precision ani false-positive rate bez zweryfikowanych negatywów. Dopasowanie: sezon, klasa, venue, kategoria, sesja, nazwisko; niejednoznaczności pozostają niedopasowane. Przypadki treningowe/DSQ/DNS są oddzielone od detekcji sektorów. Jest to ocena na danych użytych w rozwoju reguł, nie niezależny test.\n");
        report.AppendLine("Numery w CSV opisują strefę punktu pomiarowego (`reported_numbered_split_zone`), nie zawsze dokładny sektor czasowy. Dlatego porównujemy wykrycie przejazdu; podajemy też strefę z CSV do ręcznej oceny lokalizacji. Nazwy skrócone w źródle są jawnie mapowane tylko w narzędziu audytu (Vali/Valentina, Hattie/Harriet itd.).\n");
        report.AppendLine("| CSV | Venue / rider | Sesja | Stan | Wskazane sektory | Strefa z CSV | Korekta Pace czasu |\n|---|---|---|---|---|---|---:|");
        var matched = new HashSet<(string, string)>();
        var detected = new HashSet<(string, string)>();
        var possible = new HashSet<(string, string)>();
        var previouslyDetected = new HashSet<(string, string)>();
        var counts = new Dictionary<string, int>();
        while (!csv.EndOfData)
        {
            var fields = csv.ReadFields()!;
            var row = headers.Select((h, i) => (h, v: i < fields.Length ? fields[i] : "")).ToDictionary(x => x.h, x => x.v);
            string Cell(string key) => row.GetValueOrDefault(key, "");
            var venue = Normalize(Cell("venue"));
            var sessionName = Cell("session").ToUpperInvariant();
            var candidates = bundle.PaceSessions.Where(s =>
            {
                var meta = sessions[s.SessionId];
                var round = meta.Object("rounds");
                var candidateVenue = Normalize(round?.Object("tracks")?.String("venue") ?? "");
                var sessionMatches = sessionName == "QUALIFYING"
                    ? s.SessionType is PaceSessionType.Qualifying or PaceSessionType.Qualifying1
                    : sessionName switch
                    {
                        "FINAL" => s.SessionType == PaceSessionType.Final,
                        "Q1" => s.SessionType == PaceSessionType.Qualifying1,
                        "Q2" => s.SessionType == PaceSessionType.Qualifying2,
                        _ => false
                    };
                var competition = round?.Object("competition_levels")?.String("code");
                return sessionMatches && round?.Int("season_year")?.ToString() == Cell("season") &&
                    (venue == candidateVenue || candidateVenue.Contains(venue)) &&
                    meta.Object("categories")?.String("code") == Cell("category") &&
                    ClassKey(competition) == ClassKey(Cell("competition_class")) &&
                    RiderKey(names[s.Run.RiderId]) == RiderKey(Cell("rider_name"));
            }).ToArray();
            var status = sessionName is "PRACTICE" or "TIMED_TRAINING" or "UNKNOWN"
                ? "Poza sesjami ocenianymi / brak określonej sesji"
                : candidates.Length != 1 ? $"Niedopasowane ({candidates.Length})" : "";
            string sectors = "—", correction = "—";
            if (candidates.Length == 1)
            {
                var candidate = candidates[0];
                var key = (candidate.SessionId, candidate.Run.RiderId);
                if (candidate.Run.Status?.ToUpperInvariant() is "DNS" or "DSQ") status = "DNS / DSQ — bez korekty";
                else if (scores.TryGetValue(key, out var score))
                {
                    matched.Add(key);
                    if (baseline?.GetValueOrDefault(key)?.IncidentSectorIndex is not null) previouslyDetected.Add(key);
                    var strong = score.Sectors.Where(s => s.Anomaly?.Kind == SectorAnomalyKind.LikelyIncident).ToArray();
                    var uncertain = score.Sectors.Any(s => s.Anomaly?.Kind == SectorAnomalyKind.PossibleIncident);
                    if (strong.Length > 0) detected.Add(key);
                    else if (uncertain) possible.Add(key);
                    status = strong.Length > 0 ? "Silna anomalia" : uncertain ? "Możliwa anomalia" : "Niewykryty";
                    if (candidate.Run.Status?.ToUpperInvariant() == "DNF") status += " (DNF)";
                    sectors = string.Join(", ", strong.Select(s => "S" + (s.SectorIndex + 1)));
                    correction = (candidate.PaceFinish?.RemovedLossSeconds ?? 0).ToString("0.000", CultureInfo.InvariantCulture) + " s";
                }
            }
            counts[status] = counts.GetValueOrDefault(status) + 1;
            report.AppendLine($"| {Cell("incident_record_id")} | {Cell("venue")} / {Cell("rider_name")} | {sessionName} | {status} | {sectors} | {Cell("sector_index")} | {correction} |");
        }
        report.AppendLine($"\nUnikalne dopasowane przejazdy oceniane: {matched.Count}; silna anomalia: {detected.Count}; tylko możliwa: {possible.Count}; niewykryte: {matched.Count - detected.Count - possible.Count}. DNF bez zlokalizowanej anomalii nie jest zaliczony jako wykryty sektor.\n");
        if (baseline is not null) report.AppendLine($"Silne wskazania przed zmianą: {previouslyDetected.Count}, po zmianie: {detected.Count}, na dokładnie tych samych dopasowanych przejazdach.\n");
        foreach (var item in counts) report.AppendLine($"- {item.Key}: {item.Value} wpisów CSV.");
        await File.WriteAllTextAsync(Path.Combine(output, "confirmed-incidents-2026.md"), report.ToString());
        Console.WriteLine($"Confirmed CSV: {matched.Count} unique runs; {detected.Count} strong, {possible.Count} possible. Full mapping in confirmed-incidents-2026.md.");
    }

    private static string Normalize(string value) => new(value.Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c))
        .Select(char.ToUpperInvariant).ToArray());

    private static string ClassKey(string? value) => Normalize(value ?? "") switch
    {
        "WC" or "WORLDCUP" => "WC",
        "WCH" or "WORLDCHAMPIONSHIP" or "WORLDCHAMPIONSHIPS" or "WORLDCHAMPS" => "WCH",
        var other => other
    };

    // CSV aliases only. These affect label matching, never production detection.
    private static string RiderKey(string name) => Normalize(name) switch
    {
        "VALIHOLL" => "VALENTINAHOLL",
        "ANGELSUAREZ" => "ANGELSUAREZALONSO",
        "RICHIERUDE" => "RICHARDRUDEJR",
        "HATTIEHARNDEN" => "HARRIETHARNDEN",
        "ROSAMARIEJENSEN" => "ROSAJENSEN",
        var other => other
    };
}
