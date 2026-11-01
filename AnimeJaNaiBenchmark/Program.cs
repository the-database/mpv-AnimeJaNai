using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AnimeJaNaiBenchmark;

var invariant = CultureInfo.InvariantCulture;
using var cancelled = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancelled.Cancel(); };
try
{
    if (args.Contains("--help"))
    {
        Console.WriteLine("AnimeJaNaiBenchmark [--install-root DIR] [--mode native-display|pipeline]");
        Console.WriteLine("  [--resolutions 480x360,640x480,768x576,1280x720,1920x1080]");
        Console.WriteLine("  [--slots 1010,1011] [--rounds 3] [--seconds 10] [--warmup-seconds 3]");
        Console.WriteLine("  [--output-dir DIR] [--video FILE --start-seconds 144] [--quick]");
        return 0;
    }
    var valueOptions = new HashSet<string> { "--install-root", "--mode", "--resolutions", "--slots",
        "--rounds", "--seconds", "--warmup-seconds", "--output-dir", "--video", "--start-seconds" };
    var options = new Dictionary<string, string>();
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--quick") continue;
        if (!valueOptions.Contains(args[i]) || i + 1 >= args.Length || args[i + 1].StartsWith("--"))
            throw new ArgumentException($"Unknown option or missing value: {args[i]}");
        options.Add(args[i], args[++i]);
    }
    string Option(string name, string fallback) => options.GetValueOrDefault(name, fallback);
    double Number(string name, double fallback) => double.Parse(Option(name, fallback.ToString(invariant)), invariant);
    bool windows = OperatingSystem.IsWindows(), quick = args.Contains("--quick");
    string installRoot = Path.GetFullPath(Option("--install-root",
        Environment.GetEnvironmentVariable("ANIMEJANAI_ROOT") ?? Path.Combine(AppContext.BaseDirectory, "..", "..")));
    string root = Path.Combine(installRoot, "animejanai"), inference = Path.Combine(root, "inference");
    string bench = Path.Combine(root, "benchmarks"), conf = Path.Combine(root, "animejanai.conf");
    string mode = Option("--mode", "native-display");
    if (mode is not ("native-display" or "pipeline")) throw new ArgumentException("Mode must be native-display or pipeline.");
    string[] resolutions = Option("--resolutions", quick ? "480x360" :
        "480x360,640x480,768x576,1280x720,1920x1080").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    int[] slots = Option("--slots", "1010,1011").Split(',').Select(s => int.Parse(s, invariant)).ToArray();
    int rounds = int.Parse(Option("--rounds", quick ? "1" : "3"), invariant);
    double seconds = Number("--seconds", quick ? 3 : 10), warmup = Number("--warmup-seconds", 3);
    double startSeconds = Number("--start-seconds", 0);
    if (rounds is < 1 or > 10 || !double.IsFinite(seconds) || seconds is < 1 or > 120 ||
        !double.IsFinite(warmup) || warmup is < 0 or > 30 || !double.IsFinite(startSeconds) || startSeconds < 0)
        throw new ArgumentException("Use 1-10 rounds, 1-120 measured seconds, 0-30 warmup seconds, and a nonnegative start.");
    if (slots.Length == 0 || resolutions.Length == 0 || slots.Distinct().Count() != slots.Length ||
        resolutions.Distinct().Count() != resolutions.Length)
        throw new ArgumentException("Select at least one distinct slot and input resolution.");
    string? video = options.GetValueOrDefault("--video");
    if (video != null && resolutions.Length != 1) throw new ArgumentException("--video requires a single --resolutions value.");
    string backend = IniValue(conf, "global", "backend") ?? "TensorRT";
    bool d3d = windows && (backend.Equals("DirectML", StringComparison.OrdinalIgnoreCase) ||
        backend.Equals("ncnn", StringComparison.OrdinalIgnoreCase));
    string player = windows ? Path.Combine(installRoot, "mpvnet.exe") : Path.Combine(installRoot, "mpv", "mpv");
    string Native(string name) => Path.Combine(inference, windows ? name + ".exe" : name);
    string Library(string name) => Path.Combine(inference, windows ? name + ".dll" : "lib" + name + ".so");
    string Name(int slot) => slot switch
    {
        1010 => "Balanced",
        1011 => "Performance",
        _ => IniValue(conf, $"slot_{slot}", "profile_name") ?? $"slot {slot}",
    };
    string run = Path.GetFullPath(Option("--output-dir", Path.Combine(root, "benchmark-results",
        DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", invariant))));
    Directory.CreateDirectory(run);
    File.Copy(conf, Path.Combine(run, "animejanai.conf"), overwrite: true);
    var samples = new List<Sample>();
    string? outputResolution = null;
    double? displayHz = null;

    Console.WriteLine($"AnimeJaNai benchmark v2 - backend: {backend}");
    Console.WriteLine(mode == "native-display"
        ? "Native-display rendering: fullscreen gpu-next with your shaders/debanding, presentation sync disabled."
        : "Pipeline throughput: hardware decode + upscaling, without rendering.");
    if (mode == "native-display")
        Console.WriteLine("Video windows will open fullscreen. Leave them visible; resizing/minimizing invalidates the run.");
    Console.WriteLine($"Each sample: {warmup}s warmup, {seconds}s measured. {rounds} alternating rounds.");
    Console.WriteLine($"Engines prepare before timing. Raw measurements: {run}");

    foreach (string res in resolutions)
    {
        var match = Regex.Match(res, @"^([0-9]{2,5})x([0-9]{2,5})$");
        if (!match.Success) throw new ArgumentException($"Invalid input resolution: {res}");
        int width = int.Parse(match.Groups[1].Value, invariant), height = int.Parse(match.Groups[2].Value, invariant);
        string clipDir = Path.Combine(run, "clips", res);
        Directory.CreateDirectory(clipDir);
        string clip = video != null ? Path.GetFullPath(video) : Path.Combine(clipDir, res + ".mp4");
        if (video == null) File.Copy(Path.Combine(bench, res + ".mp4"), clip, overwrite: true);
        if (backend.Equals("TensorRT", StringComparison.OrdinalIgnoreCase))
        {
            string dummy = Path.Combine(run, "build-input.nv12");
            File.WriteAllBytes(dummy, new byte[checked(width * height * 3 / 2)]);
            foreach (int slot in slots)
            {
                Console.WriteLine($"Preparing {Name(slot)} @ {res}");
                await Run(Native("aji_harness"), new[] { "--input", dummy, "--width", width.ToString(invariant),
                    "--height", height.ToString(invariant), "--frames", "1", "--conf", conf,
                    "--model-dir", Path.Combine(root, "onnx"), "--rife-model-dir", Path.Combine(root, "rife"),
                    "--trtexec", Native("trtexec"), "--slot", slot.ToString(invariant) },
                    $"build-{res}-{slot}", 1800);
            }
        }
        for (int round = 1; round <= rounds; round++)
        {
            foreach (int slot in round % 2 == 0 ? slots.Reverse() : slots)
            {
                string tag = $"{res}-{slot}-{mode}-{round}";
                string reportPath = Path.Combine(run, tag + ".player.json"), trace = Path.Combine(run, tag + ".trace");
                string control = Path.Combine(run, tag + ".control.json");
                File.WriteAllText(control, JsonSerializer.Serialize(new { width, height,
                    warmup_seconds = warmup, seconds, report = reportPath }));
                string Quote(string value) => "%" + Encoding.UTF8.GetByteCount(value) + "%" + value;
                string vf = "@aji:animejanai:lib=" + Quote(Library("aji")) + ":conf=" + Quote(conf) +
                    ":model-dir=" + Quote(Path.Combine(root, "onnx")) +
                    ":rife-model-dir=" + Quote(Path.Combine(root, "rife")) + ":trtexec=" + Quote(Native("trtexec")) +
                    ":stats=" + Quote(Path.Combine(run, tag + ".model.log")) + $":slot={slot}:passthrough=no";
                var playerArgs = new List<string> { "--load-scripts=no",
                    "--config-dir=" + Path.Combine(installRoot, "portable_config"),
                    "--idle=no", "--keep-open=no", "--loop-file=inf", "--pause=no",
                    "--resume-playback=no", "--save-position-on-quit=no", "--cache=no", "--cache-pause=no",
                    "--osd-level=0", "--osd-bar=no", "--input-default-bindings=no",
                    "--hwdec=" + (d3d ? "d3d11va" : "nvdec"), "--start=" + startSeconds.ToString(invariant),
                    "--vf=" + vf, "--script=" + Path.Combine(bench, "benchmark-measure.lua"),
                    "--dump-stats=" + trace, "--log-file=" + Path.Combine(run, tag + ".mpv.log"),
                    "--untimed=yes", "--no-audio", "--sid=no", "--framedrop=no", "--video-sync=audio" };
                if (windows) playerArgs.AddRange(new[] { "--process-instance=multi", "--auto-load-folder=no" });
                // Match animejanai_backend.lua, disabled along with the other playback scripts.
                if (d3d) playerArgs.Add("--gpu-api=d3d11");
                if (mode == "pipeline") playerArgs.AddRange(new[] { "--vo=null", "--vo-null-accept-hwframes=yes", "--force-window=no" });
                else playerArgs.AddRange(new[] { "--vo=gpu-next", "--force-render=yes", "--fullscreen=yes",
                    "--window-minimized=no", "--hidpi-window-scale=no",
                    "--vulkan-swap-mode=immediate", "--opengl-swapinterval=0", "--d3d11-sync-interval=0" }
                    .Where(a => windows || !a.StartsWith("--d3d11-")));
                playerArgs.AddRange(new[] { "--", clip });
                await Run(player, playerArgs, tag, seconds + warmup + 120,
                    new Dictionary<string, string> { ["AJ_BENCH_CONFIG"] = control });
                using var json = JsonDocument.Parse(File.ReadAllText(reportPath));
                JsonElement report = json.RootElement;
                if (report.GetProperty("status").GetString() != "ok")
                    throw new InvalidDataException($"Rejected {tag}: {report.GetProperty("error")}");
                if (mode == "pipeline" && report.GetProperty("output").GetProperty("pixelformat").GetString() != (d3d ? "d3d11" : "cuda"))
                    throw new InvalidDataException($"Rejected {tag}: pipeline output must remain on the GPU; update the bundled player.");
                var measurement = FrameTrace.Measure(File.ReadLines(trace),
                    report.GetProperty("start_seconds").GetDouble(), report.GetProperty("end_seconds").GetDouble());
                if (measurement.Seconds < seconds * 0.8)
                    throw new InvalidDataException($"Insufficient output coverage: {tag}");
                if (report.GetProperty("dropped_frames").GetInt32() != 0 ||
                    report.GetProperty("decoder_dropped_frames").GetInt32() != 0)
                    throw new InvalidDataException($"Dropped frames invalidate the throughput result: {tag}");
                if (report.GetProperty("vo").GetString() != (mode == "pipeline" ? "null" : "gpu-next"))
                    throw new InvalidDataException($"Unexpected video output: {tag}");
                if (mode == "native-display")
                {
                    var viewport = report.GetProperty("viewport");
                    int w = viewport.GetProperty("w").GetInt32(), h = viewport.GetProperty("h").GetInt32();
                    if (w < 10 || h < 10) throw new InvalidDataException($"Renderer has no usable viewport: {tag}");
                    string size = $"{w}x{h}";
                    if (outputResolution != null && outputResolution != size)
                        throw new InvalidDataException($"Renderer size changed from {outputResolution} to {size}.");
                    outputResolution = size;
                    if (report.TryGetProperty("display_hz", out var hz) && hz.ValueKind == JsonValueKind.Number &&
                        double.IsFinite(hz.GetDouble()) && hz.GetDouble() > 0)
                        displayHz ??= hz.GetDouble();
                }
                samples.Add(new(Name(slot), res, round, measurement, report.Clone()));
                File.WriteAllText(Path.Combine(run, "samples.json"),
                    JsonSerializer.Serialize(samples, BenchmarkResult.JsonOptions));
                Console.WriteLine($"{Name(slot)} @ {res}, round {round}: {measurement.Fps:F2} FPS " +
                    $"({measurement.Frames} output intervals / {measurement.Seconds:F6}s)");
            }
        }
    }
    var metadata = new MeasurementMetadata("mpv-output-trace-v2", mode, outputResolution, displayHz);
    string table = BenchmarkResult.Format(backend, metadata, samples, slots.Select(Name).ToArray(), resolutions);
    File.WriteAllText(Path.Combine(run, "benchmark.txt"), table);
    File.WriteAllText(Path.Combine(root, "benchmark.txt"), table);
    Console.WriteLine("\n" + table);
    Console.WriteLine($"Completed. Evidence: {run}");
    return 0;

    async Task Run(string exe, IEnumerable<string> arguments, string tag, double timeoutSeconds,
        Dictionary<string, string>? environment = null)
    {
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = installRoot, UseShellExecute = false,
            CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in arguments) psi.ArgumentList.Add(arg);
        if (!windows)
            psi.Environment["LD_LIBRARY_PATH"] = Path.Combine(installRoot, "mpv") + ":" + inference + ":" +
                Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        if (environment != null)
            foreach (var pair in environment) psi.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(psi) ?? throw new IOException($"Could not start {exe}");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancelled.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0) throw new IOException($"{tag} exited with code {process.ExitCode}. Logs: {run}");
        }
        catch (OperationCanceledException) when (!cancelled.IsCancellationRequested)
        {
            throw new TimeoutException($"{tag} timed out. Logs: {run}");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await File.WriteAllTextAsync(Path.Combine(run, tag + ".stdout.log"), await stdout);
            await File.WriteAllTextAsync(Path.Combine(run, tag + ".stderr.log"), await stderr);
        }
    }
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

static string? IniValue(string path, string section, string key)
{
    bool active = false;
    foreach (string raw in File.ReadLines(path))
    {
        string line = raw.Trim();
        if (line.StartsWith('[') && line.EndsWith(']'))
            active = line[1..^1].Equals(section, StringComparison.OrdinalIgnoreCase);
        else if (active && line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
            return line[(key.Length + 1)..].Trim();
    }
    return null;
}
