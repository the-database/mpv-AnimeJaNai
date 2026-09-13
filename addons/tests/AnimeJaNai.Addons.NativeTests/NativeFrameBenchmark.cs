using System.Diagnostics;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Native;

internal static class NativeFrameBenchmark
{
    public static async Task RunAsync(string root, string output, List<JsonObject> evidence)
    {
        string source = await NativeFrameChecks.CreatePatternAsync(root, output);
        string config = Path.Combine(output, "animejanai.conf");
        File.WriteAllText(config, "[global]\nconfig_version=3\nbackend=DirectML\nlogging=no\ndefault_slot=1002\n");
        var modes = new[] { "none", "inactive", "64x36", "320x180" };
        using var self = Process.GetCurrentProcess();
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (string mode in pass == 0 ? modes : modes.Reverse())
            {
                using var frames = mode == "none" ? null : new NativeFrameBuffer();
                using var player = new NativePlayback(root, source, config, output, 1002, "DirectML", frames?.Handle ?? 0);
                using var subscription = mode is "none" or "inactive" ? null : frames!.Subscribe(mode == "64x36" ? new(64, 36, 30) : new(320, 180, 60));
                JsonObject state = new();
                var warmup = Stopwatch.StartNew();
                while (warmup.Elapsed < TimeSpan.FromSeconds(2))
                {
                    state = player.Poll(); subscription?.ReadLatest();
                    if (player.Failed) throw new Exception("Benchmark playback failed: " + state);
                }
                if (state["outputWidth"]?.GetValue<long>() != 960) throw new Exception("Benchmark did not reach 2x DirectML.");
                double startPosition = state["positionSeconds"]!.GetValue<double>();
                long startDecode = state["decoderDroppedFrames"]?.GetValue<long>() ?? 0;
                long startOutput = state["outputDroppedFrames"]?.GetValue<long>() ?? 0;
                self.Refresh(); double cpu = self.TotalProcessorTime.TotalMilliseconds;
                int count = 0; long bytes = 0;
                var window = Stopwatch.StartNew();
                while (window.Elapsed < TimeSpan.FromSeconds(4))
                {
                    state = player.Poll();
                    if (player.Failed) throw new Exception("Benchmark playback failed: " + state);
                    var sample = subscription?.ReadLatest();
                    if (sample is not null) { count++; bytes += sample.Pixels.Length; }
                }
                self.Refresh(); cpu = self.TotalProcessorTime.TotalMilliseconds - cpu;
                double elapsed = window.Elapsed.TotalSeconds;
                var result = new JsonObject {
                    ["mode"] = mode, ["pass"] = pass + 1, ["wallSeconds"] = elapsed, ["nativeAndReaderCpuMs"] = cpu,
                    ["mediaSecondsAdvanced"] = state["positionSeconds"]!.GetValue<double>() - startPosition,
                    ["samplesRead"] = count, ["binaryBytesRead"] = bytes,
                    ["decoderDrops"] = (state["decoderDroppedFrames"]?.GetValue<long>() ?? 0) - startDecode,
                    ["outputDrops"] = (state["outputDroppedFrames"]?.GetValue<long>() ?? 0) - startOutput,
                    ["input"] = "480x360 24fps synthetic moving colors", ["output"] = "960x720 DirectML, null output",
                };
                evidence.Add(result);
                Console.WriteLine(result.ToJsonString());
                if ((mode is "64x36" or "320x180") && count < 10) throw new Exception("Benchmark did not read active samples.");
            }
        }
    }
}
