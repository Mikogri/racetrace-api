using System.Diagnostics;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

var folder = Path.GetFullPath(args[0]);
var rows = JsonSerializer.Deserialize<float[][]>(File.ReadAllText(Path.Combine(folder, "onnx-inputs.json")))!;
var expected = JsonSerializer.Deserialize<double[]>(File.ReadAllText(Path.Combine(folder, "onnx-expected.json")))!;
var runtimeExpected = JsonSerializer.Deserialize<double[]>(File.ReadAllText(Path.Combine(folder, "onnx-runtime-expected.json")))!;
var options = new SessionOptions { IntraOpNumThreads = 2 };
var sessions = Enumerable.Range(0, 3)
    .Select(i => new InferenceSession(Path.Combine(folder, $"lightgbm-bag-{i}.onnx"), options)).ToArray();
try
{
    var features = new DenseTensor<float>(rows.SelectMany(r => r).ToArray(), [rows.Length, rows[0].Length]);
    var inputs = new[] { NamedOnnxValue.CreateFromTensor("features", features) };
    var scores = new double[rows.Length];
    var timer = Stopwatch.StartNew();
    foreach (var session in sessions)
    {
        using var result = session.Run(inputs);
        var probabilities = result.Last().AsTensor<float>();
        for (var i = 0; i < rows.Length; i++) scores[i] += probabilities[i, 1] / 3.0;
    }
    var elapsed = timer.Elapsed.TotalMilliseconds;
    var difference = scores.Zip(runtimeExpected, (a, b) => Math.Abs(a - b)).Max();
    var nativeDifference = scores.Zip(expected, (a, b) => Math.Abs(a - b)).Max();
    if (difference > 2e-5) throw new InvalidOperationException($"Python/C# score mismatch: {difference}");
    var summary = new { Runs = rows.Length, PythonOnnxVsCSharpOnnxDifference = difference,
        PythonNativeVsCSharpOnnxDifference = nativeDifference, NativeParityPassed = nativeDifference <= 2e-5,
        Tolerance = 2e-5, Milliseconds = elapsed };
    File.WriteAllText(Path.Combine(folder, "csharp-onnx-parity.json"), JsonSerializer.Serialize(summary));
    Console.WriteLine(JsonSerializer.Serialize(summary));
}
finally
{
    foreach (var session in sessions) session.Dispose();
    options.Dispose();
}
