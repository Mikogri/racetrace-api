using System.Collections;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;

if (args.Length < 2) throw new ArgumentException("Usage: NativeParity <benchmark-folder> <production-api.dll>");
var folder = Path.GetFullPath(args[0]);
using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "native-packs.json")));
var timings = new List<object>();
var scoreComparisons = 0;
var maximum = 0d;
var changed = 0;
foreach (var pack in document.RootElement.EnumerateArray())
{
    var rows = pack.GetProperty("rows").GetInt32();
    var columns = pack.GetProperty("columns").GetInt32();
    var bytes = File.ReadAllBytes(Path.Combine(folder, pack.GetProperty("features").GetString()!));
    if (!BitConverter.IsLittleEndian || bytes.Length != rows * columns * sizeof(double))
        throw new InvalidDataException("Expected an exact little-endian float64 feature matrix.");
    var features = MemoryMarshal.Cast<byte, double>(bytes.AsSpan()).ToArray();
    if (features.Any(v => !double.IsFinite(v))) throw new InvalidDataException("Nonfinite exported input.");
    var model = new TreeModel(Path.Combine(folder, pack.GetProperty("model").GetString()!));
    var threshold = pack.GetProperty("threshold").GetDouble();
    var expected = pack.GetProperty("expected").EnumerateArray().Select(v => v.GetDouble()).ToArray();
    for (var i = 0; i < rows; i++)
    {
        var value = model.Predict(features.AsSpan(i * columns, columns));
        maximum = Math.Max(maximum, Math.Abs(value - expected[i]));
        if ((value > threshold) != (expected[i] > threshold)) changed++;
        scoreComparisons++;
    }
    var durations = new double[15];
    double consumed = 0;
    for (var repeat = 0; repeat < durations.Length; repeat++)
    {
        var timer = Stopwatch.StartNew();
        for (var i = 0; i < rows; i++) consumed += model.Predict(features.AsSpan(i * columns, columns));
        durations[repeat] = timer.Elapsed.TotalMilliseconds;
    }
    GC.KeepAlive(consumed);
    Array.Sort(durations);
    timings.Add(new { Split = pack.GetProperty("split").GetString(), Rows = rows, Columns = columns,
        MedianMs = durations[7], P95Ms = durations[13] + .3 * (durations[14] - durations[13]), SamplesSortedMs = durations });
}
if (maximum > 2e-12 || changed != 0) throw new InvalidOperationException($"Native score parity failed: max={maximum:R}, decisions={changed}.");
var fullSetTimings = new List<object>();
if (File.Exists(Path.Combine(folder, "full-inputs.json")))
{
    using var full = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "full-inputs.json")));
    var info = full.RootElement;
    var bytes = File.ReadAllBytes(Path.Combine(folder, info.GetProperty("file").GetString()!));
    var features = MemoryMarshal.Cast<byte, double>(bytes.AsSpan()).ToArray();
    var columns = info.GetProperty("columns").GetInt32();
    if (features.Length != info.GetProperty("rows").GetInt32() * columns) throw new InvalidDataException("Full matrix mismatch.");
    var model = new TreeModel(Path.Combine(folder, info.GetProperty("model").GetString()!));
    foreach (var subset in info.GetProperty("subsets").EnumerateObject())
    {
        var indices = subset.Value.EnumerateArray().Select(v => v.GetInt32()).ToArray();
        var samples = new double[15];
        double consumed = 0;
        foreach (var i in indices) consumed += model.Predict(features.AsSpan(i * columns, columns));
        for (var repeat = 0; repeat < samples.Length; repeat++)
        {
            var timer = Stopwatch.StartNew();
            foreach (var i in indices) consumed += model.Predict(features.AsSpan(i * columns, columns));
            samples[repeat] = timer.Elapsed.TotalMilliseconds;
        }
        GC.KeepAlive(consumed);
        Array.Sort(samples);
        fullSetTimings.Add(new { Subset = subset.Name, Runs = indices.Length, MedianMs = samples[7],
            P95Ms = samples[13] + .3 * (samples[14] - samples[13]), SamplesSortedMs = samples });
    }
}

// Production assembly is built separately from the exact committed tree. The
// delegate avoids reflection dispatch inside the detector-only timing loop.
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[1]));
var sectorType = assembly.GetType("RaceTrace.Api.Services.PacePrecomputedSector", true)!;
var optionsType = assembly.GetType("RaceTrace.Api.Services.PaceOptions", true)!;
var options = Activator.CreateInstance(optionsType, nonPublic: true)!;
var method = assembly.GetType("RaceTrace.Api.Services.SectorIncidentDetector", true)!
    .GetMethod("Analyze", BindingFlags.Static | BindingFlags.NonPublic)!;
if (method.GetParameters().Length != 2) throw new InvalidOperationException("Expected committed production detector signature.");
var parameter = Expression.Parameter(typeof(object));
var call = Expression.Call(method, Expression.Convert(parameter, sectorType.MakeArrayType()), Expression.Constant(options, optionsType));
var analyze = Expression.Lambda<Func<object, IEnumerable>>(Expression.Convert(call, typeof(IEnumerable)), parameter).Compile();
var wanted = document.RootElement[0].GetProperty("keys").EnumerateArray().Select(v => v.GetString()!).ToHashSet();
// The first fold does not contain every row: union held rows across all six years.
wanted.Clear();
foreach (var pack in document.RootElement.EnumerateArray().Where(p => p.GetProperty("split").GetString()!.StartsWith("leave-")))
    foreach (var key in pack.GetProperty("keys").EnumerateArray().Skip(pack.GetProperty("calibration_rows").GetInt32())) wanted.Add(key.GetString()!);
var inputs = new List<(string Key, object Sectors)>();
var root = Directory.GetParent(folder)!.Parent!.FullName;
for (var year = 2021; year <= 2026; year++)
{
    using var input = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "artifacts", "incident-multiseason", year.ToString(), $"inputs-{year}.json")));
    foreach (var row in input.RootElement.GetProperty("PaceSessions").EnumerateArray())
    {
        var key = row.GetProperty("SessionId").GetString() + "/" + row.GetProperty("Run").GetProperty("RiderId").GetString();
        if (wanted.Contains(key)) inputs.Add((key, JsonSerializer.Deserialize(row.GetProperty("Sectors"), sectorType.MakeArrayType())!));
    }
}
if (inputs.Count != 20340 || inputs.Select(r => r.Key).Distinct().Count() != inputs.Count) throw new InvalidDataException("Production corpus mismatch.");
var flags = new Dictionary<string, bool>();
foreach (var input in inputs)
    flags[input.Key] = analyze(input.Sectors).Cast<object>().Any(a => a.GetType().GetProperty("Kind")!.GetValue(a)!.ToString() == "LikelyIncident");
var detectorMs = new double[11];
for (var repeat = 0; repeat < detectorMs.Length; repeat++)
{
    var timer = Stopwatch.StartNew();
    foreach (var input in inputs) GC.KeepAlive(analyze(input.Sectors));
    detectorMs[repeat] = timer.Elapsed.TotalMilliseconds;
}
Array.Sort(detectorMs);
var productionDifferences = 0;
foreach (var pack in document.RootElement.EnumerateArray().Where(p => p.GetProperty("split").GetString()!.StartsWith("leave-")))
{
    var keys = pack.GetProperty("keys").EnumerateArray().Select(v => v.GetString()!).ToArray();
    var expectedFlags = pack.GetProperty("production").EnumerateArray().Select(v => v.GetBoolean()).ToArray();
    for (var i = pack.GetProperty("calibration_rows").GetInt32(); i < keys.Length; i++)
        if (flags[keys[i]] != expectedFlags[i]) productionDifferences++;
}
var result = new { NativeScoresCompared = scoreComparisons, MaximumScoreDifference = maximum,
    ChangedLearnedDecisions = changed, PreparedLearnedScoring = timings, FullSetPreparedScoring = fullSetTimings, ProductionRuns = inputs.Count,
    ProductionAlarms = flags.Count(r => r.Value), FrozenProductionFlagDifferences = productionDifferences,
    ProductionDetectorMedianMs = detectorMs[5], ProductionDetectorSamplesSortedMs = detectorMs,
    ApiIntegration = false, Scope = "Native model/production detector CPU only; excludes API, database, feature construction and network." };
File.WriteAllText(Path.Combine(folder, "csharp-native-benchmark.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(result));

sealed class TreeModel
{
    private readonly Node[][] _trees;

    public TreeModel(string path)
    {
        using var model = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = model.RootElement;
        if (root.GetProperty("num_tree_per_iteration").GetInt32() != 1 || root.GetProperty("average_output").GetBoolean() ||
            root.GetProperty("objective").GetString() != "binary sigmoid:1") throw new InvalidDataException("Unsupported model objective.");
        _trees = root.GetProperty("tree_info").EnumerateArray().Select(tree =>
        {
            var nodes = new List<Node>();
            int Add(JsonElement value)
            {
                var index = nodes.Count;
                nodes.Add(default);
                if (value.TryGetProperty("leaf_value", out var leaf)) nodes[index] = new(-1, leaf.GetDouble(), 0, 0, false, false);
                else
                {
                    if (value.GetProperty("decision_type").GetString() != "<=") throw new InvalidDataException("Categorical split unsupported.");
                    var left = Add(value.GetProperty("left_child"));
                    var right = Add(value.GetProperty("right_child"));
                    nodes[index] = new(value.GetProperty("split_feature").GetInt32(), value.GetProperty("threshold").GetDouble(),
                        left, right, value.GetProperty("default_left").GetBoolean(), value.GetProperty("missing_type").GetString() == "Zero");
                }
                return index;
            }
            Add(tree.GetProperty("tree_structure"));
            return nodes.ToArray();
        }).ToArray();
    }

    public double Predict(ReadOnlySpan<double> row)
    {
        double margin = 0;
        foreach (var tree in _trees)
        {
            var index = 0;
            while (tree[index].Feature >= 0)
            {
                var node = tree[index];
                var value = row[node.Feature];
                var left = double.IsNaN(value) || node.ZeroMissing && value == 0 ? node.DefaultLeft : value <= node.Value;
                index = left ? node.Left : node.Right;
            }
            margin += tree[index].Value;
        }
        return 1 / (1 + Math.Exp(-margin));
    }

    private readonly record struct Node(int Feature, double Value, int Left, int Right, bool DefaultLeft, bool ZeroMissing);
}
