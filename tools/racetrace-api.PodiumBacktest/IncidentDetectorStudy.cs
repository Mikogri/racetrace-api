using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Services;
using static RaceTrace.Api.PodiumBacktest.ConfirmedIncidentAudit;

namespace RaceTrace.Api.PodiumBacktest;

internal sealed record IncidentStudyLabel(
    string Id,
    int Year,
    string Event,
    string Rider,
    string Category,
    string Session
);

internal static class IncidentDetectorStudy
{
    internal static IReadOnlyList<IncidentStudyLabel> ReadMarkdown(string text, int year)
    {
        var output = new List<IncidentStudyLabel>();
        string[] headers = [];
        var heading = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('#'))
                heading = line.TrimStart('#').Trim();
            if (!line.StartsWith('|'))
                continue;
            var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Contains("Zawodnik"))
            {
                headers = cells;
                continue;
            }
            if (
                headers.Length == 0
                || cells.Length != headers.Length
                || !cells.Any(c => c is "`CRASH`" or "`OFF_COURSE`")
            )
                continue;
            string Cell(string name) =>
                Array.IndexOf(headers, name) is var i && i >= 0 ? cells[i] : "";
            var category = Normalize(
                Cell("Kategoria") is { Length: > 0 } value ? value : Cell("Kat.")
            ) switch
            {
                "ELITEMEN" or "ME" => "EM",
                "ELITEWOMEN" or "WE" => "EW",
                "JUNIORMEN" or "MJ" => "JM",
                "JUNIORWOMEN" or "WJ" => "JW",
                var other => other,
            };
            output.Add(
                new(
                    $"{year}-{output.Count + 1:000}",
                    year,
                    heading,
                    Cell("Zawodnik"),
                    category,
                    Cell("Sesja")
                )
            );
        }
        return output;
    }

    internal static PaceSessionType[] SessionTypes(string value) =>
        Normalize(value) switch
        {
            "FINAL" => [PaceSessionType.Final],
            "SEMIFINAL" => [PaceSessionType.SemiFinal],
            "QUALIFYING" or "QUALI" => [PaceSessionType.Qualifying, PaceSessionType.Qualifying1],
            "Q1" => [PaceSessionType.Qualifying1],
            "Q2" => [PaceSessionType.Qualifying2],
            "SEEDING" => [PaceSessionType.Seeding],
            _ => [],
        };

    internal static bool MatchesEvent(IncidentStudyLabel label, JsonObject round)
    {
        var name = Normalize(label.Event);
        var championship = name.Contains("WCH") || name.Contains("MISTRZOSTWASWIATA");
        if ((ClassKey(round.Object("competition_levels")?.String("code")) == "WCH") != championship)
            return false;
        var hint = Regex.Replace(label.Event, @"^\d+\.\s*", "").Split('—', '(')[0].Split('/')[0];
        hint = Regex.Replace(hint, @"#\d+", "");
        if (!(Normalize(round.Object("tracks")?.String("venue") ?? "")).Contains(Normalize(hint)))
            return false;
        return label.Year != 2021
            || !name.Contains("SNOWSHOE")
            || round.Int("round_number")
                == (
                    label.Event.Contains("#1") ? 5
                    : label.Event.Contains("#2") ? 6
                    : -1
                );
    }

    internal static async Task RunAsync(string[] args)
    {
        string Argument(string key, string fallback) =>
            Array.IndexOf(args, key) is var i && i >= 0 ? args[i + 1] : fallback;
        var root = Path.GetFullPath(Argument("--input", "artifacts/incident-multiseason"));
        var documents = Path.GetFullPath(Argument("--documents", ""));
        var output = Path.GetFullPath(Argument("--output", "artifacts/incident-csharp"));
        Directory.CreateDirectory(output);
        var runs = new List<StudyRun>();
        var labels = new List<IncidentStudyLabel>();
        var hashes = new Dictionary<string, string>();
        async Task<string> Read(string path)
        {
            var bytes = await File.ReadAllBytesAsync(path);
            hashes[path] = Convert.ToHexString(SHA256.HashData(bytes));
            return Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        }
        for (var year = 2021; year <= 2026; year++)
        {
            var bundle = JsonSerializer.Deserialize<RiderDetailDataBundle>(
                await Read(Path.Combine(root, year.ToString(), $"inputs-{year}.json"))
            )!;
            var baseline = JsonSerializer
                .Deserialize<BaselineRun[]>(
                    await Read(Path.Combine(root, year.ToString(), "baseline.json"))
                )!
                .ToDictionary(r => (r.SessionId, r.RiderId), r => r.Result);
            var sessions = bundle.Data.Sessions.ToDictionary(s => s.String("id")!);
            var names = bundle
                .Data.Results.GroupBy(r => r.String("rider_id")!)
                .ToDictionary(g => g.Key, g => g.First().Object("riders")!.DisplayName());
            runs.AddRange(
                bundle
                    .PaceSessions.Where(s => baseline.ContainsKey((s.SessionId, s.Run.RiderId)))
                    .Select(s => new StudyRun(
                        year,
                        s,
                        sessions[s.SessionId],
                        names[s.Run.RiderId],
                        baseline[(s.SessionId, s.Run.RiderId)]
                    ))
            );
            if (year < 2026)
                labels.AddRange(
                    ReadMarkdown(
                        await Read(Path.Combine(documents, $"RaceTrace_Incidents_{year}.md")),
                        year
                    )
                );
        }
        var csvPath = Path.GetFullPath(
            Argument(
                "--labels-2026",
                "tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv"
            )
        );
        await Read(csvPath);
        using (
            var csv = new TextFieldParser(csvPath, Encoding.UTF8)
            {
                HasFieldsEnclosedInQuotes = true,
            }
        )
        {
            csv.SetDelimiters(",");
            var headers = csv.ReadFields()!;
            while (!csv.EndOfData)
            {
                var fields = csv.ReadFields()!;
                string Cell(string name) => fields[Array.IndexOf(headers, name)];
                labels.Add(
                    new(
                        Cell("incident_record_id"),
                        2026,
                        Cell("venue") + " (" + Cell("competition_class") + ")",
                        Cell("rider_name"),
                        Cell("category"),
                        Cell("session")
                    )
                );
            }
        }
        var index = runs.GroupBy(r =>
                (r.Year, RiderKey(r.Name), r.Meta.Object("categories")?.String("code"))
            )
            .ToDictionary(g => g.Key, g => g.ToArray());
        var matching = labels
            .Select(label =>
            {
                var types = SessionTypes(label.Session);
                var candidates = index
                    .GetValueOrDefault((label.Year, RiderKey(label.Rider), label.Category), [])
                    .Where(r =>
                        types.Contains(r.Input.SessionType)
                        && MatchesEvent(label, r.Meta.Object("rounds")!)
                    )
                    .ToArray();
                return new MatchedLabel(
                    label,
                    types.Length == 0 ? "training/ambiguous"
                        : candidates.Length == 1 ? "matched"
                        : $"{candidates.Length} scored runs (DNS/DSQ or unavailable result)",
                    candidates.Length == 1 ? candidates[0].Key : null
                );
            })
            .ToArray();
        var positives = matching
            .Where(m => m.State == "matched")
            .Select(m => m.Key!.Value)
            .ToHashSet();
        var baselineHits = runs.Where(r =>
                r.Before.Sectors.Any(s => s.Anomaly?.Kind == SectorAnomalyKind.LikelyIncident)
            )
            .Select(r => r.Key)
            .ToHashSet();
        Console.WriteLine(
            $"Sources: {labels.Count} rows; {positives.Count} distinct scored positives; {runs.Count} runs."
        );
        foreach (var m in matching.Where(m => m.Label.Year == 2026 && m.State != "matched"))
            Console.WriteLine(
                $"Unscored: {m.Label.Id} {m.Label.Rider}, {m.Label.Event}, {m.Label.Session}: {m.State}"
            );
        var pace = new PaceOptions();
        var controls = NegativeControls();
        var candidatesToTest = new List<IncidentDetectionOptions> { new() };
        if (args.Contains("--sweep"))
            foreach (var seconds in new[] { 10.0, 15.0, 20.0 })
            foreach (var ratio in new[] { .25, .35, .50 })
                candidatesToTest.Add(
                    new() { ExtremeLossSeconds = seconds, ExtremeLossRatio = ratio }
                );
        var experiments = new List<Experiment>();
        foreach (var options in candidatesToTest)
        {
            var timer = Stopwatch.StartNew();
            var hits = runs.Where(r =>
                    SectorIncidentDetector
                        .Analyze(r.Input.Sectors, pace, options)
                        .Any(s => s.Kind == SectorAnomalyKind.LikelyIncident)
                )
                .Select(r => r.Key)
                .ToHashSet();
            var safe = controls.All(s =>
                !SectorIncidentDetector
                    .Analyze(s, pace, options)
                    .Any(a => a.Kind == SectorAnomalyKind.LikelyIncident)
            );
            var experiment = new Experiment(
                options,
                hits,
                hits.Count(positives.Contains),
                safe,
                timer.Elapsed.TotalMilliseconds
            );
            experiments.Add(experiment);
            Console.WriteLine(
                $"{experiments.Count}/{candidatesToTest.Count}: {experiment.Hits}/{positives.Count} labelled; {hits.Count} alarms; controls={safe}; {timer.Elapsed.TotalSeconds:F2}s"
            );
        }
        // Report the actual production defaults, not a recall-maximizing candidate.
        var selected = experiments[0];
        if (!selected.Safe)
            throw new InvalidOperationException(
                "Production detector failed the regression controls."
            );
        var allKeys = runs.Select(r => r.Key).ToHashSet();
        var folds = Enumerable
            .Range(2021, 6)
            .Select(year =>
            {
                var held = runs.Where(r => r.Year == year).Select(r => r.Key).ToHashSet();
                var train = allKeys.Except(held).ToHashSet();
                var budget = (int)Math.Ceiling(1.15 * baselineHits.Count(train.Contains));
                var chosen = experiments
                    .Where(e => e.Safe && e.Detected.Count(train.Contains) <= budget)
                    .OrderByDescending(e =>
                        e.Detected.Count(k => train.Contains(k) && positives.Contains(k))
                    )
                    .ThenBy(e => e.Detected.Count(train.Contains))
                    .First();
                return new
                {
                    Year = year,
                    Hits = chosen.Detected.Count(k => held.Contains(k) && positives.Contains(k)),
                    Positives = positives.Count(held.Contains),
                    Alarms = chosen.Detected.Count(held.Contains),
                    chosen.Options,
                };
            })
            .ToArray();
        var before = positives.Count(baselineHits.Contains);
        var noTiming = runs.Count(r =>
            positives.Contains(r.Key) && !r.Input.Sectors.Any(s => s.TimeSeconds is > 0)
        );
        var dnf = runs.Where(r =>
                string.Equals(r.Input.Run.Status?.Trim(), "DNF", StringComparison.OrdinalIgnoreCase)
            )
            .Select(r => r.Key)
            .ToHashSet();
        var summary = new
        {
            SourceRows = labels.Count,
            Positives = positives.Count,
            Runs = runs.Count,
            NoTiming = noTiming,
            BeforeHits = before,
            BeforeAlarms = baselineHits.Count,
            AfterHits = selected.Hits,
            AfterAlarms = selected.Detected.Count,
            BeforeWithPossible = runs.Count(r =>
                positives.Contains(r.Key)
                && r.Before.Sectors.Any(s =>
                    s.Anomaly?.Kind
                        is SectorAnomalyKind.LikelyIncident
                            or SectorAnomalyKind.PossibleIncident
                )
            ),
            AfterWithPossible = runs.Count(r =>
                positives.Contains(r.Key)
                && SectorIncidentDetector
                    .Analyze(r.Input.Sectors, pace, selected.Options)
                    .Any(a =>
                        a.Kind
                            is SectorAnomalyKind.LikelyIncident
                                or SectorAnomalyKind.PossibleIncident
                    )
            ),
            BeforeWithOfficialDnf = positives.Count(k =>
                baselineHits.Contains(k) || dnf.Contains(k)
            ),
            AfterWithOfficialDnf = positives.Count(k =>
                selected.Detected.Contains(k) || dnf.Contains(k)
            ),
            FinishedPositives = positives.Count(k => !dnf.Contains(k)),
            FinishedHits = selected.Detected.Count(k => positives.Contains(k) && !dnf.Contains(k)),
            Gained = selected.Detected.Count(k =>
                positives.Contains(k) && !baselineHits.Contains(k)
            ),
            Lost = baselineHits.Count(k => positives.Contains(k) && !selected.Detected.Contains(k)),
            Selected = selected.Options,
            Default = new
            {
                experiments[0].Hits,
                Alarms = experiments[0].Detected.Count,
                experiments[0].Safe,
            },
            WholeYearHits = folds.Sum(f => f.Hits),
            WholeYearAlarms = folds.Sum(f => f.Alarms),
            Folds = folds,
            Experiments = experiments.Select(e => new
            {
                e.Options,
                e.Hits,
                Alarms = e.Detected.Count,
                e.Safe,
                e.Milliseconds,
            }),
            InputHashes = hashes,
        };
        await File.WriteAllTextAsync(
            Path.Combine(output, "summary.json"),
            JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true })
        );
        var report = new StringBuilder("# Incident detection — C# study, 2021–2026\n\n");
        report.AppendLine(
            "Uses the actual C# detector and frozen session/category inputs. Source lists are incomplete, positive-only and not independently reverified. Coverage is NOT precision. A run hit does not verify the exact incident sector. No provider requests, database writes, official result edits or frontend changes.\n"
        );
        report.AppendLine(
            $"Source rows: {labels.Count}; distinct matched positives: {positives.Count}; official scored runs: {runs.Count}; positives without any measured sector: {noTiming}.\n"
        );
        report.AppendLine(
            $"Before: {before}/{positives.Count} hits, {baselineHits.Count} alarms. Selected: {selected.Hits}/{positives.Count} hits, {selected.Detected.Count} alarms. Gained {summary.Gained}, lost {summary.Lost} labelled runs.\n"
        );
        report.AppendLine(
            $"Including uncertain timing warnings: {summary.AfterWithPossible}/{positives.Count} (before: {summary.BeforeWithPossible}/{positives.Count}). Warnings are not automatic exclusions or confirmed physical crashes.\n"
        );
        report.AppendLine(
            $"Finished labelled runs: {summary.FinishedHits}/{summary.FinishedPositives} localized. Separately, including the official DNF status as run-level evidence gives {summary.AfterWithOfficialDnf}/{positives.Count} (before: {summary.BeforeWithOfficialDnf}/{positives.Count}). This does NOT locate a missing incident sector or reconstruct an unfinished run.\n"
        );
        report.AppendLine(
            "Selected means production defaults, not the candidate with the highest recall. Optional exploratory whole-year selection limits alarms to 115% of the old detector on training years. Synthetic controls cover uniform slow pace, a single exceptional sector, gradual variation, wide fields, small terminal losses and displaced checkpoints. These controls are not a labelled negative population.\n"
        );
        report.AppendLine(
            "| Year | Labelled runs | Before | Selected | Selected alarms |\n|---|---:|---:|---:|---:|"
        );
        foreach (var year in Enumerable.Range(2021, 6))
        {
            var keys = runs.Where(r => r.Year == year).Select(r => r.Key).ToHashSet();
            report.AppendLine(
                $"| {year} | {positives.Count(keys.Contains)} | {baselineHits.Count(k => keys.Contains(k) && positives.Contains(k))} | {selected.Detected.Count(k => keys.Contains(k) && positives.Contains(k))} | {selected.Detected.Count(keys.Contains)} |"
            );
        }
        report.AppendLine(
            $"\nWhole-year selection stress test: {summary.WholeYearHits}/{positives.Count} held-out hits; {summary.WholeYearAlarms} alarms. Each fold selects thresholds on other years only; categories/sessions of an event stay together. The same rider can appear in different years. Since the model was designed using known examples across these years, this is exploratory transfer evidence, not an untouched external test.\n"
        );
        report.AppendLine(
            "Selected options:\n```json\n"
                + JsonSerializer.Serialize(
                    selected.Options,
                    new JsonSerializerOptions { WriteIndented = true }
                )
                + "\n```\n"
        );
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), report.ToString());
        var labelCsv = new StringBuilder(
            "record,year,event,rider,category,session,state,session_id,rider_id,before,after\n"
        );
        foreach (var m in matching)
            labelCsv.AppendLine(
                string.Join(
                    ",",
                    new object?[]
                    {
                        m.Label.Id,
                        m.Label.Year,
                        m.Label.Event,
                        m.Label.Rider,
                        m.Label.Category,
                        m.Label.Session,
                        m.State,
                        m.Key?.Session,
                        m.Key?.Rider,
                        m.Key is { } key && baselineHits.Contains(key),
                        m.Key is { } afterKey && selected.Detected.Contains(afterKey),
                    }.Select(Csv)
                )
            );
        await File.WriteAllTextAsync(Path.Combine(output, "labels.csv"), labelCsv.ToString());
        var sectorCsv = new StringBuilder(
            "year,session_id,rider_id,rider,venue,category,status,known_positive,sector,time,expected,loss_seconds,dimensionless_deviation,classification,sector_evidence_allows_correction\n"
        );
        foreach (var r in runs)
        {
            var anomalies = SectorIncidentDetector.Analyze(r.Input.Sectors, pace, selected.Options);
            for (var i = 0; i < anomalies.Count; i++)
                if (anomalies[i].Kind != SectorAnomalyKind.Normal)
                    sectorCsv.AppendLine(
                        string.Join(
                            ",",
                            new object?[]
                            {
                                r.Year,
                                r.Key.Session,
                                r.Key.Rider,
                                r.Name,
                                r.Meta.Object("rounds")?.Object("tracks")?.String("venue"),
                                r.Meta.Object("categories")?.String("code"),
                                r.Input.Run.Status,
                                positives.Contains(r.Key),
                                i + 1,
                                r.Input.Sectors[i].TimeSeconds,
                                anomalies[i].ExpectedSeconds,
                                anomalies[i].LossSeconds,
                                anomalies[i].RobustDeviation,
                                anomalies[i].Kind,
                                anomalies[i].AllowsFinishCorrection,
                            }.Select(Csv)
                        )
                    );
        }
        await File.WriteAllTextAsync(Path.Combine(output, "sectors.csv"), sectorCsv.ToString());
        Console.WriteLine(
            $"Before {before}/{positives.Count}; selected {selected.Hits}/{positives.Count}; report: {output}"
        );
    }

    private static string Csv(object? value) =>
        "\""
        + (
            value is IFormattable f
                ? f.ToString(null, CultureInfo.InvariantCulture)
                : value?.ToString()
        )?.Replace("\"", "\"\"")
        + "\"";

    private static PacePrecomputedSector[][] NegativeControls()
    {
        var stats = new PaceFieldSummary(30, 50, Math.Log(50), .01, .5, 48, 49);
        PacePrecomputedSector[] Profile(double[] times) =>
            times
                .Select((t, i) => new PacePrecomputedSector(i, t, i == 4 ? 25 : 1, stats))
                .ToArray();
        return
        [
            Profile([80, 80, 80, 80, 80]),
            Profile([50, 50, 50, 50, 53.11]),
            Profile([50, 80, 20, 50, 50]),
            Profile([40, 50, 50, 50, 50]),
            Profile([50, 52, 54, 56, 58]),
            Profile([50, 50, 54, 50, 50])
                .Select(s => s with { Statistics = stats with { LogMadScale = .12 } })
                .ToArray(),
        ];
    }

    private sealed record BaselineRun(
        string SessionId,
        string RiderId,
        RiderSessionPaceResult Result
    );

    private sealed record StudyRun(
        int Year,
        PacePrecomputedSessionData Input,
        JsonObject Meta,
        string Name,
        RiderSessionPaceResult Before
    )
    {
        internal (string Session, string Rider) Key => (Input.SessionId, Input.Run.RiderId);
    }

    private sealed record MatchedLabel(
        IncidentStudyLabel Label,
        string State,
        (string Session, string Rider)? Key
    );

    private sealed record Experiment(
        IncidentDetectionOptions Options,
        HashSet<(string Session, string Rider)> Detected,
        int Hits,
        bool Safe,
        double Milliseconds
    );
}
