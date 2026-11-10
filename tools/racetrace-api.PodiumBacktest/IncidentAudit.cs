using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using RaceTrace.Api.Configuration;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.PodiumBacktest;

// Read-only audit using the same precomputed inputs and calculator as ranking/details.
internal static class IncidentAudit
{
    public static async Task RunAsync(string[] args)
    {
        string Argument(string key, string fallback) =>
            Array.IndexOf(args, key) is var i && i >= 0 ? args[i + 1] : fallback;
        var api = Path.GetFullPath(Argument("--api", "racetrace-api"));
        var output = Path.GetFullPath(Argument("--output", "artifacts/incident-audit"));
        var year = int.Parse(Argument("--year", "2026"));
        Directory.CreateDirectory(output);
        var inputPath = Path.Combine(output, $"inputs-{year}.json");
        RiderDetailDataBundle bundle;
        if (args.Contains("--offline"))
        {
            bundle = JsonSerializer.Deserialize<RiderDetailDataBundle>(
                await File.ReadAllTextAsync(inputPath)
            )!;
            // A frozen export may include finish references from the old detector.
            // Retain measurements/statistics, not calculator-dependent results.
            bundle = bundle with
            {
                PaceSessions = bundle
                    .PaceSessions.Select(s =>
                        s with
                        {
                            PaceFinish = null,
                            PreparedAnomalies = null,
                        }
                    )
                    .ToArray(),
            };
        }
        else
        {
            var config = JsonNode.Parse(
                await File.ReadAllTextAsync(Path.Combine(api, "appsettings.Development.json"))
            )!;
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            var client = new SupabaseRestClient(
                http,
                Options.Create(config["Supabase"]!.Deserialize<SupabaseOptions>()!)
            );
            var domain = new DomainQueryService(client);
            var sessions = (
                await client.GetAsync(
                    new SupabaseQuery("sessions")
                        .Select("id,round_categories!inner(rounds!inner(season_year))")
                        .Filter("round_categories.rounds.season_year", "eq", year)
                        .Limit(1000)
                )
            ).Data;
            bundle =
                await domain.GetRankingPreaggregatesAsync(sessions)
                ?? throw new InvalidOperationException(
                    "Incomplete preaggregation; audit would omit sessions."
                );
            await File.WriteAllTextAsync(inputPath, JsonSerializer.Serialize(bundle));
        }
        var calculator = new DownhillPaceCalculator();
        bundle = bundle with { PaceSessions = calculator.PrepareField(bundle.PaceSessions) };
        var results = bundle
            .PaceSessions.Where(s =>
                s.Run.Status?.ToUpperInvariant() is not ("DNS" or "DSQ")
                && calculator.GetConfiguredSessionWeight(s.SessionType) > 0
            )
            .Select(s => new AuditRun(
                s.SessionId,
                s.Run.RiderId,
                calculator.CalculatePrecomputedSession(s)
            ))
            .ToArray();
        var name = Argument("--name", "current");
        await File.WriteAllTextAsync(
            Path.Combine(output, $"{name}.json"),
            JsonSerializer.Serialize(results)
        );
        var baselinePath = Path.Combine(output, Argument("--baseline", "before-csv.json"));
        await WriteReportAsync(bundle, results, output, year, baselinePath);
        var baseline = File.Exists(baselinePath)
            ? JsonSerializer
                .Deserialize<AuditRun[]>(await File.ReadAllTextAsync(baselinePath))!
                .ToDictionary(r => (r.SessionId, r.RiderId), r => r.Result)
            : null;
        if (args.Contains("--confirmed"))
            await ConfirmedIncidentAudit.WriteAsync(
                Argument("--confirmed", ""),
                bundle,
                results.ToDictionary(r => (r.SessionId, r.RiderId), r => r.Result),
                output,
                baseline
            );
        if (args.Contains("--verify-details"))
            await VerifyDetailsAsync(api, bundle);
        var compared = 0;
        var diagnosticDifferences = 0;
        var paceDifferences = 0;
        var parityRows = new StringBuilder("session_id,rider_id,raw_pace,precomputed_pace\n");
        foreach (var group in bundle.PaceSessions.GroupBy(s => s.SessionId))
        {
            var first = group.First();
            var raw = calculator
                .CalculateSession(
                    new(
                        first.SessionType,
                        group.Select(s => s.Run).ToArray(),
                        first.CompetitionClassPriority
                    )
                )
                .ToDictionary(r => r.RiderId);
            foreach (var s in group.Where(s => raw.ContainsKey(s.Run.RiderId)))
            {
                var precomputed = calculator.CalculatePrecomputedSession(s);
                compared++;
                if (
                    !raw[s.Run.RiderId]
                        .Sectors.Select(v => v.Anomaly?.Kind)
                        .SequenceEqual(precomputed.Sectors.Select(v => v.Anomaly?.Kind))
                )
                    diagnosticDifferences++;
                if (
                    raw[s.Run.RiderId].Pace.HasValue != precomputed.Pace.HasValue
                    || Math.Abs((raw[s.Run.RiderId].Pace ?? 0) - (precomputed.Pace ?? 0)) > 1e-7
                )
                {
                    paceDifferences++;
                    parityRows.AppendLine(
                        string.Join(
                            ",",
                            s.SessionId,
                            s.Run.RiderId,
                            raw[s.Run.RiderId].Pace?.ToString("R", CultureInfo.InvariantCulture),
                            precomputed.Pace?.ToString("R", CultureInfo.InvariantCulture)
                        )
                    );
                    if (paceDifferences <= 5)
                        Console.WriteLine(
                            $"Parity difference session={s.SessionId} rider={s.Run.RiderId}: raw={raw[s.Run.RiderId].Pace:R}, precomputed={precomputed.Pace:R}"
                        );
                }
            }
        }
        Console.WriteLine(
            $"Raw field vs preaggregation parity: {compared} runs, {diagnosticDifferences} differing classifications, {paceDifferences} differing Pace values."
        );
        await File.WriteAllTextAsync(
            Path.Combine(output, $"parity-differences-{year}.csv"),
            parityRows.ToString()
        );
        if (diagnosticDifferences != 0 || paceDifferences != 0)
            throw new InvalidOperationException(
                "Raw/precomputed results differ; inspect before using the report."
            );
        Console.WriteLine(
            $"{bundle.Data.Sessions.Count} sessions; {results.Length} official runs; {results.Count(r => r.Result.IncidentSectorIndex.HasValue)} runs with incidents. Saved {output}/{name}.json"
        );
    }

    private sealed record AuditRun(string SessionId, string RiderId, RiderSessionPaceResult Result);

    private static async Task VerifyDetailsAsync(string api, RiderDetailDataBundle bundle)
    {
        var config = JsonNode.Parse(
            await File.ReadAllTextAsync(Path.Combine(api, "appsettings.Development.json"))
        )!;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        var client = new SupabaseRestClient(
            http,
            Options.Create(config["Supabase"]!.Deserialize<SupabaseOptions>()!)
        );
        var calculator = new DownhillPaceCalculator();
        var targets = bundle
            .PaceSessions.Where(s => s.SessionId == "113" && s.Run.RiderId is "15" or "183" or "60")
            .Concat(
                bundle
                    .PaceSessions.Where(s =>
                        s.Run.RiderId == "113" && s.SessionType == PaceSessionType.Final
                    )
                    .Take(2)
            )
            .ToArray();
        foreach (var expected in targets)
        {
            var response = await client.RpcAsync(
                "get_rider_detail_data",
                new JsonObject
                {
                    ["p_rider_id"] = long.Parse(expected.Run.RiderId),
                    ["p_session_ids"] = new JsonArray(long.Parse(expected.SessionId)),
                },
                SupabaseRole.Anon,
                CancellationToken.None
            );
            var actual = DomainQueryService
                .ParseRiderDetailDataBundle(response)
                .PaceSessions.Single();
            var old = calculator.CalculatePrecomputedSession(expected);
            var detail = calculator.CalculatePrecomputedSession(actual);
            if (
                actual.PaceFinish is null
                || detail.Pace.HasValue != old.Pace.HasValue
                || Math.Abs((detail.Pace ?? 0) - (old.Pace ?? 0)) > 1e-7
            )
                throw new InvalidOperationException(
                    $"Detail mismatch session={expected.SessionId} rider={expected.Run.RiderId}, ranking={old.Pace} details={detail.Pace}"
                );
            Console.WriteLine(
                $"Detail parity session={expected.SessionId} rider={expected.Run.RiderId}: Pace={detail.Pace:F6}; corrected finish={actual.PaceFinish.TimeSeconds:F6}"
            );
        }
    }

    private static async Task WriteReportAsync(
        RiderDetailDataBundle bundle,
        AuditRun[] runs,
        string output,
        int year,
        string baselinePath
    )
    {
        var sessions = bundle.Data.Sessions.ToDictionary(s => s.String("id")!);
        var riders = bundle
            .Data.Results.GroupBy(r => r.String("rider_id")!)
            .ToDictionary(g => g.Key, g => g.First().Object("riders")!.DisplayName());
        var inputs = bundle.PaceSessions.ToDictionary(s => (s.SessionId, s.Run.RiderId));
        var flags = runs.SelectMany(r =>
                r.Result.Sectors.Where(s => s.Anomaly is { Kind: not SectorAnomalyKind.Normal })
                    .Select(s => (Run: r, Sector: s, Session: sessions[r.SessionId]))
            )
            .ToArray();
        var csv = new StringBuilder(
            "RoundId;RoundNumber;Venue;Category;Session;SessionId;SessionDate;Rider;RiderId;ResultStatus;FinishPosition;Sector;Status;TimeSeconds;ExpectedSeconds;EstimatedLossSeconds;RobustDeviation;Reason;Verification\n"
        );
        static string Number(double? value) =>
            value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
        static string Escape(object? value) =>
            "\"" + (value?.ToString() ?? "").Replace("\"", "\"\"") + "\"";
        foreach (var f in flags)
        {
            var a = f.Sector.Anomaly!;
            var input = inputs[(f.Run.SessionId, f.Run.RiderId)];
            csv.AppendLine(
                string.Join(
                    ";",
                    new object?[]
                    {
                        f.Session.String("round_id"),
                        f.Session.Object("rounds")?.Int("round_number"),
                        f.Session.Object("rounds")?.Object("tracks")?.String("venue"),
                        f.Session.Object("categories")?.String("code"),
                        f.Session.String("name"),
                        f.Run.SessionId,
                        f.Session.String("session_date"),
                        riders[f.Run.RiderId],
                        f.Run.RiderId,
                        input.Run.Status,
                        input.Run.FinishRank,
                        f.Sector.SectorIndex + 1,
                        a.Kind,
                        Number(f.Sector.Time),
                        Number(a.ExpectedSeconds),
                        Number(a.LossSeconds),
                        Number(a.RobustDeviation),
                        a.Reason,
                        "",
                    }.Select(Escape)
                )
            );
        }
        await File.WriteAllTextAsync(
            Path.Combine(output, $"incidents-{year}.csv"),
            csv.ToString(),
            new UTF8Encoding(true)
        );
        var report = new StringBuilder($"# Incydenty sektorowe — {year}\n\n");
        report.AppendLine(
            $"Wygenerowano: {DateTimeOffset.UtcNow:O}. Dane: zapis z bazy, {bundle.Data.Sessions.Count} sesji, {runs.Length} analizowanych przejazdów z oficjalnych sesji. Treningi, DNS i DSQ nie są analizowane.\n"
        );
        report.AppendLine(
            "To wskazania algorytmu, **nie potwierdzone upadki**. Przyczyną może być błąd, zatrzymanie, problem techniczny, zmiana warunków lub pomiar. Szacowana strata to różnica względem oczekiwanego czasu tego ridera, nie gap do lidera. Sektor S1 oznacza odcinek od startu do pierwszego pomiaru; ostatni sektor może prowadzić do mety.\n"
        );
        foreach (
            var kind in new[]
            {
                SectorAnomalyKind.LikelyIncident,
                SectorAnomalyKind.PossibleIncident,
                SectorAnomalyKind.InvalidTiming,
            }
        )
        {
            var subset = flags.Where(f => f.Sector.Anomaly!.Kind == kind).ToArray();
            report.AppendLine(
                $"- {kind}: **{subset.Length} sektorów**, **{subset.Select(f => (f.Run.SessionId, f.Run.RiderId)).Distinct().Count()} przejazdów**."
            );
        }
        report.AppendLine(
            "\nKategorie są rozdzielone w statystykach sesji. Detektor zachowuje dotychczasowe silne wskazania i dodaje odporny profil czasowy oraz kontrolowane odniesienie do rang. Wykrycie nie wymaga dwóch innych zmierzonych sektorów; bez osobistego odniesienia wymagany jest ekstremalny odstający czas. Korekta mety nadal wymaga niezależnych czystych sektorów, kompletnego sklasyfikowanego przejazdu i zgodności sumy z metą. RobustDeviation jest bezwymiarowe, LossSeconds jest w sekundach. Żadna z tych wartości nie jest prawdopodobieństwem fizycznego upadku. Bieżące progi i ograniczenia: docs/ALGORITHMS.md.\n"
        );
        report.AppendLine(
            "## Przejazdy z silnym wskazaniem według rundy\n\n| ID rundy w bazie | Venue | Przejazdy |\n|---|---|---:|"
        );
        foreach (
            var group in flags
                .Where(f => f.Sector.Anomaly!.Kind == SectorAnomalyKind.LikelyIncident)
                .GroupBy(f => f.Session.String("round_id"))
        )
            report.AppendLine(
                $"| {group.Key} | {group.First().Session.Object("rounds")?.Object("tracks")?.String("venue")} | {group.Select(f => (f.Run.SessionId, f.Run.RiderId)).Distinct().Count()} |"
            );
        report.AppendLine(
            "\n## Finały — silne wskazania do weryfikacji\n\n| ID rundy / venue | Kategoria | Rider | Sektor | Czas | Oczekiwany | Strata | Session / rider ID |\n|---|---|---|---|---:|---:|---:|---|"
        );
        foreach (
            var f in flags.Where(f =>
                f.Run.Result.SessionType == PaceSessionType.Final
                && f.Sector.Anomaly!.Kind == SectorAnomalyKind.LikelyIncident
            )
        )
            report.AppendLine(
                $"| {f.Session.String("round_id")} / {f.Session.Object("rounds")?.Object("tracks")?.String("venue")} | {f.Session.Object("categories")?.String("code")} | {riders[f.Run.RiderId]} | S{f.Sector.SectorIndex + 1} | {Number(f.Sector.Time)} | {Number(f.Sector.Anomaly!.ExpectedSeconds)} | +{Number(f.Sector.Anomaly.LossSeconds)} s | {f.Run.SessionId} / {f.Run.RiderId} |"
            );
        report.AppendLine(
            $"\nPełna lista (także kwalifikacje, niejednoznaczne przypadki i podejrzane pomiary): `incidents-{year}.csv`. Kolumna `Verification` jest pusta — do ręcznej oceny.\n"
        );
        if (File.Exists(baselinePath))
        {
            var baseline = JsonSerializer.Deserialize<AuditRun[]>(
                await File.ReadAllTextAsync(baselinePath)
            )!;
            var old = baseline.ToDictionary(r => (r.SessionId, r.RiderId));
            var pairs = runs.Where(r => old.ContainsKey((r.SessionId, r.RiderId)))
                .Select(r => (Old: old[(r.SessionId, r.RiderId)].Result, New: r.Result))
                .ToArray();
            var differences = pairs
                .Where(p => p.Old.Pace.HasValue && p.New.Pace.HasValue)
                .Select(p => p.New.Pace!.Value - p.Old.Pace!.Value)
                .Where(d => Math.Abs(d) > 1e-8)
                .ToArray();
            report.AppendLine("## Wpływ na Pace pojedynczych sesji\n");
            report.AppendLine(
                $"Stary detektor: {baseline.Count(r => r.Result.IncidentSectorIndex.HasValue)} przejazdów z incydentem. Nowy: {runs.Count(r => r.Result.IncidentSectorIndex.HasValue)}. Zmienione wartości Pace (obie liczbowe): {differences.Length}. Pace liczba ↔ brak: {pairs.Count(p => p.Old.Pace.HasValue != p.New.Pace.HasValue)}."
            );
            if (differences.Length > 0)
                report.AppendLine(
                    $"Zakres zmian Pace sesji: {Number(differences.Min())} do +{Number(differences.Max())}; średnia zmiana bezwzględna wśród zmienionych: {Number(differences.Average(Math.Abs))}. To nie jest zmiana końcowego ratingu/rankingu.\n"
                );
        }
        await File.WriteAllTextAsync(
            Path.Combine(output, $"incidents-{year}.md"),
            report.ToString()
        );
    }
}
