using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: CalculatorParity <baseline-api.dll> <current-api.dll>");
    return 2;
}

foreach (var path in args)
    Console.WriteLine(
        $"Assembly {Path.GetFullPath(path)}; sha256={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}"
    );
var before = Calculate(Path.GetFullPath(args[0]));
var after = Calculate(Path.GetFullPath(args[1]));
var failed = false;
foreach (var (name, expected) in before)
{
    var actual = after[name];
    var equal = StringComparer.Ordinal.Equals(expected, actual);
    Console.WriteLine(
        $"{name}: {(equal ? "EXACT MATCH" : "MISMATCH")}; bytes={Encoding.UTF8.GetByteCount(actual)}; sha256={Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actual)))}"
    );
    if (!equal)
    {
        failed = true;
        Console.Error.WriteLine($"baseline={expected}\ncurrent={actual}");
    }
}
Console.WriteLine(
    $"Compared {before.Count} deterministic result bundles (full serialized numeric outputs, no tolerance). Fixed Monte Carlo seed 17432; 5000 iterations. No database/network."
);
return failed ? 1 : 0;

static Dictionary<string, string> Calculate(string path)
{
    var load = new CalculatorContext(path);
    try
    {
        var assembly = load.LoadFromAssemblyPath(path);
        Type T(string name) =>
            assembly.GetType("RaceTrace.Api.Services." + name, throwOnError: true)!;
        object Make(string name, params (string Name, object? Value)[] values)
        {
            var supplied = values.ToDictionary(
                pair => pair.Name,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase
            );
            var constructor = T(name)
                .GetConstructors(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                )
                .Where(ctor => !ctor.GetParameters().Any(p => p.ParameterType == T(name)))
                .OrderByDescending(ctor => ctor.GetParameters().Length)
                .First();
            return constructor.Invoke(
                constructor
                    .GetParameters()
                    .Select(parameter =>
                        supplied.TryGetValue(parameter.Name!, out var value) ? value
                        : parameter.HasDefaultValue ? parameter.DefaultValue
                        : throw new InvalidOperationException(
                            $"Missing fixture argument {name}.{parameter.Name}"
                        )
                    )
                    .ToArray()
            );
        }
        Array Typed(string name, IEnumerable<object> values)
        {
            var list = values.ToArray();
            var array = Array.CreateInstance(T(name), list.Length);
            for (var i = 0; i < list.Length; i++)
                array.SetValue(list[i], i);
            return array;
        }
        object Invoke(object instance, string method, params object?[] values) =>
            instance
                .GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Single(candidate =>
                    candidate.Name == method && candidate.GetParameters().Length == values.Length
                )
                .Invoke(instance, values)!;
        object Stage(string name) => Enum.Parse(T("PaceSessionType"), name);
        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        void Capture(string name, object value) =>
            results.Add(name, JsonSerializer.Serialize(value, value.GetType()));

        var pace = Make("DownhillPaceCalculator");
        var sessions = new List<object>();
        foreach (var scenario in new[] { "clean", "incident", "dnf", "qualifying" })
        {
            var runs = Enumerable
                .Range(0, 8)
                .Select(i =>
                {
                    double?[] sectors = Enumerable
                        .Range(0, 5)
                        .Select(s => (double?)(25.0 + s * 4 + i * 0.7))
                        .ToArray();
                    if (scenario == "incident" && i == 2)
                        sectors[2] += 28;
                    if (scenario == "dnf" && i == 6)
                        sectors[4] = null;
                    return Make(
                        "PaceRiderRun",
                        ("RiderId", $"r{i}"),
                        ("SectorTimes", sectors),
                        ("TopSpeed", 65.0 - i),
                        ("FinishRank", scenario == "dnf" && i == 6 ? null : (int?)(i + 1)),
                        ("FinishTimeSeconds", scenario == "dnf" && i == 6 ? null : sectors.Sum()),
                        ("IsClassified", !(scenario == "dnf" && i == 6)),
                        ("Status", scenario == "dnf" && i == 6 ? "DNF" : null)
                    );
                });
            var session = Make(
                "PaceSessionData",
                ("SessionType", Stage(scenario == "qualifying" ? "Qualifying1" : "Final")),
                ("Runs", Typed("PaceRiderRun", runs))
            );
            var calculated = Invoke(pace, "CalculateSession", session);
            Capture("pace.field." + scenario, calculated);
            sessions.AddRange(((IEnumerable)calculated).Cast<object>());
        }
        for (var i = 0; i < 8; i++)
            Capture(
                "pace.aggregate.r" + i,
                Invoke(
                    pace,
                    "CalculatePace",
                    $"r{i}",
                    Typed("RiderSessionPaceResult", sessions),
                    4,
                    null
                )
            );

        var benchmarkRuns = Enumerable
            .Range(0, 80)
            .Select(i =>
                Make(
                    "PaceRiderRun",
                    ("RiderId", $"benchmark-{i}"),
                    (
                        "SectorTimes",
                        Enumerable
                            .Range(0, 5)
                            .Select(s => (double?)(25 + s * 4 + i * .12))
                            .ToArray()
                    ),
                    ("TopSpeed", 65.0 - i * .05),
                    ("FinishRank", (int?)(i + 1)),
                    ("FinishTimeSeconds", (double?)(165 + i * .6)),
                    ("IsClassified", true),
                    ("Status", null)
                )
            )
            .ToArray();
        var benchmarkSession = Make(
            "PaceSessionData",
            ("SessionType", Stage("Final")),
            ("Runs", Typed("PaceRiderRun", benchmarkRuns))
        );
        for (var i = 0; i < 20; i++)
            Invoke(pace, "CalculateSession", benchmarkSession);
        var samples = new double[5];
        for (var batch = 0; batch < samples.Length; batch++)
        {
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < 20; i++)
                Invoke(pace, "CalculateSession", benchmarkSession);
            samples[batch] = watch.Elapsed.TotalMilliseconds / 20;
        }
        Array.Sort(samples);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 20; i++)
            Invoke(pace, "CalculateSession", benchmarkSession);
        var bytesPerOperation = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / 20;
        Console.WriteLine(
            $"pace.benchmark.80-rider-session median={samples[2]:F3} ms/op allocated={bytesPerOperation} B/op"
        );

        var aggregateSessions = Typed("RiderSessionPaceResult", sessions);
        for (var i = 0; i < 100; i++)
            Invoke(pace, "CalculatePace", "r2", aggregateSessions, 4, null);
        var aggregateSamples = new double[5];
        for (var batch = 0; batch < aggregateSamples.Length; batch++)
        {
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < 1000; i++)
                Invoke(pace, "CalculatePace", "r2", aggregateSessions, 4, null);
            aggregateSamples[batch] = watch.Elapsed.TotalMilliseconds / 1000;
        }
        Array.Sort(aggregateSamples);
        allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
            Invoke(pace, "CalculatePace", "r2", aggregateSessions, 4, null);
        bytesPerOperation = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / 1000;
        Console.WriteLine(
            $"pace.benchmark.aggregate median={aggregateSamples[2]:F4} ms/op allocated={bytesPerOperation} B/op"
        );

        var consistency = Make("DownhillConsistencyCalculator");
        foreach (var scenario in new[] { "clean", "variable", "dnf" })
        {
            var runs = Enumerable
                .Range(0, 8)
                .Select(i =>
                {
                    var dnf = scenario == "dnf" && i is 3 or 7;
                    var sectors = Enumerable
                        .Range(0, 4)
                        .Select(s =>
                            Make(
                                "ConsistencySectorTimeInput",
                                ("SectorKey", $"s{s}"),
                                (
                                    "TimeSeconds",
                                    dnf && s == 3
                                        ? null
                                        : (double?)(
                                            30
                                            + s * 5
                                            + (scenario == "variable" ? i % 3 * 2 : i * .05)
                                        )
                                ),
                                ("IsOutlier", scenario == "variable" && i == 3 && s == 2)
                            )
                        );
                    return Make(
                        "ConsistencyRunInput",
                        ("RunId", $"run{i}"),
                        ("CourseKey", $"course{i / 2}"),
                        ("SessionType", Stage(i % 2 == 0 ? "Qualifying1" : "Final")),
                        ("Sectors", Typed("ConsistencySectorTimeInput", sectors)),
                        ("FinishRank", dnf ? null : (int?)(scenario == "variable" ? 3 + i * 2 : 3)),
                        ("FieldSize", 80),
                        ("IsClassified", !dnf),
                        ("Status", dnf ? "DNF" : null),
                        ("HasIncident", dnf)
                    );
                });
            Capture(
                "consistency." + scenario,
                Invoke(consistency, "Calculate", Typed("ConsistencyRunInput", runs), null)
            );
        }

        var rating = Make("DownhillRatingCalculator");
        foreach (var p in new[] { 0.0, 25.0, 60.0, 90.0, 100.0 })
        foreach (var c in new[] { 0.0, 50.0, 100.0 })
        foreach (var score in new double?[] { null, 50 })
            Capture(
                $"rating.{p}.{c}.{score?.ToString() ?? "null"}",
                Invoke(rating, "Calculate", p, c, score)
            );

        var options = Make("PodiumPredictionOptions");
        options.GetType().GetProperty("RandomSeed")!.SetValue(options, 17432);
        options.GetType().GetProperty("MonteCarloIterations")!.SetValue(options, 5000);
        var features = Typed(
            "PodiumPredictionFeatures",
            Enumerable
                .Range(0, 8)
                .Select(i =>
                    Make(
                        "PodiumPredictionFeatures",
                        ("RiderId", $"r{i}"),
                        ("CurrentRating", 88m - i),
                        ("TrackPace", 85m - i),
                        ("PreviousRating", 80m - i),
                        ("Rating", 88.0 - i),
                        ("Pace", 90.0 - i),
                        ("Confidence", 85.0),
                        ("Consistency", 95.0 - i),
                        ("RecentForm", i * .1),
                        ("TrackAffinity", .3),
                        ("ConditionsAffinity", .2),
                        ("WeekendPerformance", 84.0 - i),
                        ("WchPerformanceDelta", 1.0),
                        ("TitleLeverage", .3),
                        ("PressureResponse", .1),
                        ("IncidentProbability", .02 + i * .01),
                        ("EvidenceCount", 8)
                    )
                )
        );
        var calibration = Make("PodiumPredictionCalibration", ("options", options));
        var simulator = Make("MonteCarloRaceSimulator", ("options", options));
        foreach (var worlds in new[] { false, true })
        {
            var distributions = Invoke(calibration, "Calculate", features, worlds);
            Capture("podium.calibration." + worlds, distributions);
            Capture(
                "podium.simulation." + worlds,
                Invoke(simulator, "SimulateDetailed", distributions)
            );
        }
        return results;
    }
    finally
    {
        load.Unload();
    }
}

sealed class CalculatorContext(string assemblyPath) : AssemblyLoadContext(isCollectible: true)
{
    private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
}
