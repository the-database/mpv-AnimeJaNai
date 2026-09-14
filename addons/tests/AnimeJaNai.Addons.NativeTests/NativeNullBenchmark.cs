using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

internal static class NativeNullBenchmark
{
    // This measures complete uncapped 240-frame runs, including startup. It is
    // deliberately separate from the player's catalog benchmark and does not
    // change its defaults or claim a general steady-state overhead percentage.
    public static async Task RunAsync(string root, string output, List<JsonObject> evidence)
    {
        string config = Path.Combine(output, "animejanai.conf");
        File.WriteAllText(config, "[global]\nconfig_version=3\nbackend=DirectML\nlogging=no\ndefault_slot=1002\n");
        static string Quote(string value) => "%" + Encoding.UTF8.GetByteCount(value) + "%" + value;
        var parameters = new Dictionary<string, string> {
            ["lib"] = Path.Combine(root, "animejanai/inference/aji.dll"), ["conf"] = config,
            ["model-dir"] = Path.Combine(root, "animejanai/onnx"), ["rife-model-dir"] = Path.Combine(root, "animejanai/rife"),
            ["trtexec"] = Path.Combine(root, "animejanai/inference/trtexec.exe"), ["stats"] = Path.Combine(output, "inference.log") };
        string filter = "@aji:animejanai:" + string.Join(':', parameters.Select(p => p.Key + "=" + Quote(p.Value))) + ":slot=1002";
        string executable = Path.Combine(root, "mpv.exe");
        using (var file = File.OpenRead(executable)) evidence.Add(new() { ["playerSha256"] = Convert.ToHexStringLower(SHA256.HashData(file)),
            ["method"] = "240-frame end-to-end uncapped runs; startup included; one discarded warmup per mode; alternating order; DirectML slot 1002" });
        foreach (string clip in new[] { "480x360.mp4", "1920x1080.mp4" })
        {
            string source = Path.Combine(root, "animejanai/benchmarks", clip);
            for (int pass = 0; pass < 4; pass++)
            foreach (bool enabled in pass % 2 == 0 ? new[] { false, true } : new[] { true, false })
            {
                var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string argument in new[] { "--no-config", "--load-scripts=no", "--load-select=no", "--vo=null", "--hwdec=d3d11va",
                    "--untimed", "--no-audio", "--sub=no", "--frames=240", "--terminal=yes", "--msg-level=all=v", "--vf=" + filter, source })
                    info.ArgumentList.Add(argument);
                // The control omits the switch to check the actual native default.
                if (enabled) info.ArgumentList.Add("--vo-null-accept-hwframes=yes");
                var timer = Stopwatch.StartNew();
                using var process = Process.Start(info) ?? throw new IOException("Benchmark player did not start");
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                bool timedOut = false;
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)); }
                catch (TimeoutException)
                {
                    timedOut = true;
                    if (!process.HasExited) process.Kill(true);
                    await process.WaitForExitAsync();
                }
                timer.Stop();
                string text = await stdout + await stderr;
                string name = $"{Path.GetFileNameWithoutExtension(clip)}-{(enabled ? "opt-in" : "default")}-{pass}.log";
                File.WriteAllText(Path.Combine(output, name), text);
                if (timedOut) throw new TimeoutException("Benchmark player exceeded its deadline; inspect " + name);
                string dimensions = clip.StartsWith("480") ? "960x720" : "3840x2160";
                if (process.ExitCode != 0 || !text.Contains(dimensions) || !text.Contains("d3d11va"))
                    throw new Exception("Benchmark did not complete the expected DirectML workload; inspect " + name);
                var result = new JsonObject { ["clip"] = clip, ["nullHardwareFrames"] = enabled, ["warmup"] = pass == 0,
                    ["pass"] = pass, ["framesRequested"] = 240, ["wallMilliseconds"] = timer.Elapsed.TotalMilliseconds,
                    ["cpuMilliseconds"] = process.TotalProcessorTime.TotalMilliseconds, ["log"] = name, ["output"] = dimensions };
                evidence.Add(result); Console.WriteLine(result.ToJsonString());
            }
        }
    }
}
