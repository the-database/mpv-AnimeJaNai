using System.Text.Json;
using AnimeJaNaiBenchmark;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

// The old PowerShell Math.Max(0, seconds) chose its Int32 overload: 1.49s
// became 1s and 1.51s became 2s, producing artificial 1200/600 FPS bands.
foreach (long duration in new[] { 1_490_000_000L, 1_510_000_000L })
{
    const long start = 10_000_000_000;
    var trace = new List<string> { $"{start - 1} end video-flip" };
    for (int i = 0; i <= 1200; i++)
    {
        long ns = start + duration * i / 1200;
        trace.Add($"{ns} start video-flip");
        trace.Add($"{ns} end video-draw");
        trace.Add($"{ns} end video-flip");
    }
    trace.Add($"{start + duration + 1} end video-flip");
    var measured = FrameTrace.Measure(trace, 10, (start + duration) / 1e9);
    Check(measured.Frames == 1200, "Count completed output intervals only, within the warm measurement window.");
    Check(Math.Abs(measured.Seconds - duration / 1e9) < 1e-9, "Preserve fractional timing.");
    Check(Math.Abs(measured.Fps - 1200 / (duration / 1e9)) < 1e-6, "Incorrect FPS.");
}
try
{
    FrameTrace.Measure(new[] { "10000000000 end video-draw" }, 10, 20);
    throw new Exception("Missing output events must not become a score.");
}
catch (InvalidDataException) { }

using var player = JsonDocument.Parse("{}");
var samples = new[]
{
    new Sample("Balanced", "1920x1080", 1, new(100, 1, 100, 0, 0, 0, 0), player.RootElement),
    new Sample("Balanced", "1920x1080", 2, new(300, 2, 150, 0, 0, 0, 0), player.RootElement),
};
string table = BenchmarkResult.Format("TensorRT", new("mpv-output-trace-v2", "native-display", "2560x1440", 144),
    samples, new[] { "Balanced" }, new[] { "1920x1080" });
Check(table.Contains("|Balanced|133.33|"), "Pool frame counts and durations, not arithmetic means of FPS.");
string header = table.Split('\n').Single(s => s.StartsWith(BenchmarkResult.MetadataPrefix));
using var metadata = JsonDocument.Parse(header[BenchmarkResult.MetadataPrefix.Length..]);
Check(metadata.RootElement.GetProperty("output_resolution").GetString() == "2560x1440",
    "Report measured render target, independently of source or 2x model output.");
Check(metadata.RootElement.GetProperty("display_hz").GetDouble() == 144, "Preserve display refresh.");
Console.WriteLine("Benchmark timing and result format checks passed.");
