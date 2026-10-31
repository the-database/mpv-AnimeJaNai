using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnimeJaNaiBenchmark;

public sealed record MeasurementMetadata(
    string Method, string Mode, string? OutputResolution = null, double? DisplayHz = null);

public sealed record Sample(string Model, string InputResolution, int Round,
    FrameMeasurement Metrics, JsonElement Player);

public static class BenchmarkResult
{
    public const string MetadataPrefix = "Benchmark metadata: ";
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static string Format(string backend, MeasurementMetadata measurement,
        IReadOnlyList<Sample> samples, IEnumerable<string> models, IEnumerable<string> resolutions)
    {
        var jsonOptions = new JsonSerializerOptions(JsonOptions) { WriteIndented = false };
        string[] columns = resolutions.ToArray();
        var text = new StringBuilder();
        text.AppendLine($"AnimeJaNai benchmark v2 - backend: {backend}");
        // Keep metadata with its table, so a stale JSON sidecar cannot relabel a later run.
        text.AppendLine(MetadataPrefix + JsonSerializer.Serialize(measurement, jsonOptions));
        text.AppendLine();
        text.AppendLine("|fps|" + string.Join('|', columns) + "|");
        text.AppendLine("|-|" + string.Join('|', columns.Select(_ => "-")) + "|");
        foreach (string model in models)
        {
            var values = columns.Select(res =>
            {
                var cell = samples.Where(s => s.Model == model && s.InputResolution == res).ToArray();
                if (cell.Length == 0) throw new InvalidDataException($"Missing result for {model} at {res}.");
                double fps = cell.Sum(s => s.Metrics.Frames) / cell.Sum(s => s.Metrics.Seconds);
                return fps.ToString("0.00", CultureInfo.InvariantCulture);
            });
            text.AppendLine("|" + model + "|" + string.Join('|', values) + "|");
        }
        return text.ToString();
    }
}
