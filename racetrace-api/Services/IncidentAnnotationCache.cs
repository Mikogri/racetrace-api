using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure.Calculations;
using RaceTrace.Api.Infrastructure.Telemetry;

namespace RaceTrace.Api.Services;

/// <summary>Versioned detector outputs only; official timings and metric formulas remain inputs.</summary>
internal static class IncidentAnnotationCache
{
    private const int MaximumExpandedBytes = 32 * 1024 * 1024;
    private const string CachedProfile = "cached-normal-profile";
    internal static string Version { get; } =
        "incident-annotations-v1:"
        + AlgorithmCatalog.Hash(
            new
            {
                layout = "sparse-normal-v1",
                algorithm = AlgorithmCatalog.Current.Manifest["incidents"].Version,
                thresholds = AlgorithmConfiguration.Incidents,
                AlgorithmConfiguration.Pace.MinimumFieldSize,
                AlgorithmConfiguration.Pace.MinimumIncidentLossSeconds,
                AlgorithmConfiguration.Pace.IncidentRelativeLossThreshold,
                AlgorithmConfiguration.Pace.IncidentPerformanceDropThreshold,
            }
        );

    internal static IReadOnlyDictionary<
        string,
        IReadOnlyDictionary<string, SectorAnomaly[]>
    > ReadPackages(IReadOnlyList<JsonObject> packages)
    {
        var output = new Dictionary<string, IReadOnlyDictionary<string, SectorAnomaly[]>>();
        foreach (var package in packages)
        foreach (var row in (package["ia"] as JsonArray)?.OfType<JsonArray>() ?? [])
        {
            if (
                row.Count != 3
                || row[0] is null
                || row[1] is not JsonValue version
                || !version.TryGetValue<string>(out var label)
                || row[2] is not JsonValue payload
                || !payload.TryGetValue<string>(out var encoded)
            )
                continue;
            var values = Decode(label, encoded);
            if (values is not null)
                output[row[0]!.ToString()] = values;
        }
        return output;
    }

    internal static string Encode(IReadOnlyList<PacePrecomputedSessionData> runs)
    {
        using var output = new MemoryStream();
        using var raw = new MemoryStream();
        using (var writer = new BinaryWriter(raw, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0x31414349);
            writer.Write(runs.Count);
            foreach (var run in runs)
            {
                var id = Encoding.UTF8.GetBytes(run.Run.RiderId);
                if (id.Length > 256)
                    throw new InvalidDataException("Invalid cached rider identifier.");
                writer.Write((ushort)id.Length);
                writer.Write(id);
                var annotations = SectorIncidentDetector.Analyze(
                    run.Sectors.OrderBy(s => s.SectorIndex).ToArray(),
                    AlgorithmConfiguration.Pace
                );
                writer.Write(checked((ushort)annotations.Count));
                foreach (var annotation in annotations)
                {
                    var tag =
                        annotation.Kind == SectorAnomalyKind.Normal
                            ? annotation.Reason switch
                            {
                                "Within rider sector profile" => 0,
                                "Missing timing" => 1,
                                "Insufficient evidence" => 2,
                                _ => 3,
                            }
                            : 3;
                    writer.Write((byte)tag);
                    if (tag != 3)
                        continue;
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(annotation);
                    if (bytes.Length > 8192)
                        throw new InvalidDataException("Oversized cached annotation.");
                    writer.Write((ushort)bytes.Length);
                    writer.Write(bytes);
                }
            }
        }
        if (raw.Length > MaximumExpandedBytes)
            throw new InvalidDataException("Oversized incident field.");
        // Compress one buffer; tiny Brotli writes otherwise inflate normal-only fields.
        using (var compressed = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
            raw.WriteTo(compressed);
        return Convert.ToBase64String(output.ToArray());
    }

    internal static SectorAnomaly[]? Materialize(
        SectorAnomaly[] annotations,
        IReadOnlyList<PacePrecomputedSector> sectors,
        PaceOptions options
    )
    {
        var output = (SectorAnomaly[])annotations.Clone();
        for (var i = 0; i < output.Length; i++)
            if (output[i].Kind == SectorAnomalyKind.Normal && output[i].Reason == CachedProfile)
            {
                var normal = SectorIncidentDetector.DescribeCachedNormal(
                    sectors,
                    i,
                    annotations,
                    options
                );
                if (normal is null)
                    return null;
                output[i] = normal;
            }
        return output;
    }

    internal static IReadOnlyDictionary<string, SectorAnomaly[]>? Decode(
        string? version,
        string? payload
    )
    {
        if (version != Version || string.IsNullOrEmpty(payload))
            return null;
        try
        {
            if (payload.Length > 8 * 1024 * 1024)
                throw new InvalidDataException("Oversized incident cache.");
            using var input = new MemoryStream(Convert.FromBase64String(payload));
            using var compressed = new BrotliStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = compressed.Read(buffer)) > 0)
            {
                if (output.Length + read > MaximumExpandedBytes)
                    throw new InvalidDataException("Oversized expanded incident cache.");
                output.Write(buffer, 0, read);
            }
            output.Position = 0;
            using var reader = new BinaryReader(output, Encoding.UTF8, leaveOpen: true);
            if (reader.ReadInt32() != 0x31414349)
                throw new InvalidDataException("Unknown incident cache format.");
            var count = reader.ReadInt32();
            if (count is < 0 or > 100000)
                throw new InvalidDataException("Invalid cached field size.");
            var values = new Dictionary<string, SectorAnomaly[]>();
            byte[] ReadBytes(int maximum)
            {
                var length = reader.ReadUInt16();
                if (length > maximum)
                    throw new InvalidDataException("Oversized cached value.");
                var bytes = reader.ReadBytes(length);
                if (bytes.Length != length)
                    throw new InvalidDataException("Truncated incident cache.");
                return bytes;
            }
            for (var r = 0; r < count; r++)
            {
                var id = Encoding.UTF8.GetString(ReadBytes(256));
                var row = new SectorAnomaly[reader.ReadUInt16()];
                for (var i = 0; i < row.Length; i++)
                    row[i] = reader.ReadByte() switch
                    {
                        0 => new(SectorAnomalyKind.Normal, null, null, null, CachedProfile),
                        1 => new(SectorAnomalyKind.Normal, null, null, null, "Missing timing"),
                        2 => new(
                            SectorAnomalyKind.Normal,
                            null,
                            null,
                            null,
                            "Insufficient evidence"
                        ),
                        3 => JsonSerializer.Deserialize<SectorAnomaly>(ReadBytes(8192))!,
                        _ => throw new InvalidDataException("Invalid cached annotation tag."),
                    };
                if (!values.TryAdd(id, row))
                    throw new InvalidDataException("Duplicate cached rider.");
            }
            if (
                output.Position != output.Length
                || values.Values.Any(row =>
                    row.Any(a =>
                        a is null
                        || !Enum.IsDefined(a.Kind)
                        || a.Reason is null
                        || a.ExpectedSeconds is { } e && !double.IsFinite(e)
                        || a.LossSeconds is { } l && !double.IsFinite(l)
                        || a.RobustDeviation is { } z && !double.IsFinite(z)
                    )
                )
            )
                throw new InvalidDataException("Invalid incident annotations.");
            return values;
        }
        catch (Exception error)
            when (error
                    is FormatException
                        or InvalidDataException
                        or JsonException
                        or EndOfStreamException
            )
        {
            // Invalid derived data is a measured cache miss, never a substitute for source timings.
            ApiTelemetry.Cache("preaggregation", "fallback");
            return null;
        }
    }
}
