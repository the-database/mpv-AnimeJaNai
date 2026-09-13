using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons.Native;
using AnimeJaNai.Addons;

bool framesOnly = args.Length == 6 && args[^1] == "--frames-only";
bool frameBenchmark = args.Length == 4 && args[^1] == "--frames-benchmark";
bool encodingOnly = args.Length == 4 && args[^1] == "--encoding-only";
bool outputsOnly = args.Length == 6 && args[^1] == "--outputs-only";
bool remoteOnly = args.Length == 6 && args[^1] == "--remote-only";
bool capacityOnly = args.Length == 4 && args[^1] == "--capacity-only";
bool lifecycleOnly = args.Length == 4 && args[^1] == "--lifecycle-only";
if (framesOnly) args = args[..5];
if (frameBenchmark) args = args[..3];
if (encodingOnly) args = args[..3];
if (outputsOnly) args = args[..5];
if (remoteOnly) args = args[..5];
if (capacityOnly) args = args[..3];
if (lifecycleOnly) args = args[..3];
if (args.Length is not (3 or 5)) { Console.WriteLine("NativeTests <trusted-AJN-root> <new-output-directory> <dotnet.exe> [wasmtime.exe javy.exe] [--frames-only]"); return 2; }
string root = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
if (Directory.Exists(output)) { Console.Error.WriteLine("Choose a new test output directory."); return 2; }
Directory.CreateDirectory(output);
var evidence = new List<JsonObject>();
try
{
    if (remoteOnly)
    {
        await NativeRemoteInputChecks.RunAsync(root, output, new WorkerCommand(Path.GetFullPath(args[2]), [typeof(AddonWorker).Assembly.Location]), args[3], args[4], evidence);
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { passed = true, evidence }));
        return 0;
    }
    if (lifecycleOnly)
    {
        await NativeLifecycleChecks.RunAsync(root, output, evidence);
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { passed = true, evidence }));
        return 0;
    }
    if (capacityOnly)
    {
        await NativeCapacityChecks.RunAsync(root, output, new WorkerCommand(Path.GetFullPath(args[2]), [typeof(AddonWorker).Assembly.Location]), evidence);
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { passed = true, evidence }));
        return 0;
    }
    if (outputsOnly)
    {
        var outputCommand = new WorkerCommand(Path.GetFullPath(args[2]), [typeof(AddonWorker).Assembly.Location]);
        await NativeOutputChecks.RunAsync(root, output, outputCommand, args[3], args[4], evidence);
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { passed = true, evidence }));
        return 0;
    }
    if (encodingOnly)
    {
        var encodingCommand = new WorkerCommand(Path.GetFullPath(args[2]), [typeof(AddonWorker).Assembly.Location]);
        await NativeEncodingChecks.RunAsync(root, output, encodingCommand, evidence);
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { passed = true, evidence }));
        return 0;
    }
    if (frameBenchmark)
    {
        await NativeFrameBenchmark.RunAsync(root, output, evidence);
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { passed = true, evidence }));
        return 0;
    }
    if (framesOnly)
    {
        var frameCommand = new WorkerCommand(Path.GetFullPath(args[2]), [typeof(AddonWorker).Assembly.Location]);
        await NativeFrameChecks.RunAsync(root, output, frameCommand, args[3], args[4], evidence);
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { passed = true, evidence }));
        return 0;
    }
    string config = Path.Combine(output, "animejanai.conf");
    File.WriteAllText(config, "[global]\nconfig_version=3\nbackend=DirectML\nlogging=yes\ndefault_slot=1002\n");
    using var player = new NativePlayback(root, Path.Combine(root, "animejanai", "benchmarks", "480x360.mp4"), config, output, 1002, "DirectML");
    JsonObject Until(Func<JsonObject, bool> ready)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(25))
        {
            var state = player.Poll();
            if (player.Failed) throw new Exception("Native playback failed: " + state);
            if (ready(state)) { evidence.Add(state); return state; }
            if (player.Ended) throw new Exception("Native playback ended before the check: " + state);
        }
        throw new TimeoutException("Native state deadline expired.");
    }
    var active = Until(s => s["outputWidth"]?.GetValue<long>() == 960 && s["state"]?.GetValue<string>() == "running");
    if (active["inputWidth"]?.GetValue<long>() != 480 || active["decoder"]?.GetValue<string>() != "d3d11va") throw new Exception("Expected actual hardware-decoded 2x DirectML output.");
    player.Pause(true);
    Until(s => s["paused"]?.GetValue<bool>() == true);
    player.Seek(1.25);
    Until(s => s["positionSeconds"] is JsonValue v && Math.Abs(v.GetValue<double>() - 1.25) < .2 && s["seeking"]?.GetValue<bool>() == false);
    player.Pause(false);
    Until(s => s["paused"]?.GetValue<bool>() == false && s["positionSeconds"]?.GetValue<double>() > 1.4);
    player.Dispose();
    var worker = new WorkerCommand(Path.GetFullPath(args[2]), [typeof(AddonWorker).Assembly.Location]);
    string workRoot = Path.Combine(output, "processes");
    string source = Path.Combine(root, "animejanai", "benchmarks", "480x360.mp4");
    string configuration = File.ReadAllText(config);
    var opening = Stopwatch.StartNew();
    var processes = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => MediaProcess.StartAsync(root, source, configuration, 1002, "DirectML", workRoot, worker, default)));
    if (opening.Elapsed > TimeSpan.FromSeconds(2)) throw new Exception("Native opens did not return promptly behind their session handles.");
    async Task<JsonObject> StateAsync(MediaProcess session, Func<JsonObject, bool> ready)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(25))
        {
            var state = await session.GetStatusAsync(default);
            if (state["state"]?.GetValue<string>() == "failed") throw new Exception("Media worker failed: " + state);
            if (ready(state)) { evidence.Add(state); return state; }
            await Task.Delay(40);
        }
        throw new TimeoutException("Supervised media state did not become ready.");
    }
    try
    {
        foreach (var session in processes) await StateAsync(session, s => s["outputWidth"]?.GetValue<long>() == 960 && s["state"]?.GetValue<string>() == "running");
        await processes[0].PauseAsync(true, default);
        await StateAsync(processes[0], s => s["paused"]?.GetValue<bool>() == true);
        double previous = (await processes[1].GetStatusAsync(default))["positionSeconds"]!.GetValue<double>();
        await StateAsync(processes[1], s => s["positionSeconds"]?.GetValue<double>() > previous + .3);
        await processes[0].SeekAsync(2.5, default);
        await StateAsync(processes[0], s => s["positionSeconds"] is JsonValue v && Math.Abs(v.GetValue<double>() - 2.5) < .2);
        await processes[0].DisposeAsync();
        double duration = (await processes[1].GetStatusAsync(default))["durationSeconds"]!.GetValue<double>();
        await processes[1].SeekAsync(duration - .5, default);
        await StateAsync(processes[1], s => s["state"]?.GetValue<string>() == "completed");
    }
    finally
    {
        var closes = await Task.WhenAll(processes.Select(async session => { try { await session.DisposeAsync(); return (Exception?)null; } catch (Exception error) { return error; } }));
        if (closes.Any(e => e is not null)) throw new AggregateException(closes.Where(e => e is not null).Cast<Exception>());
    }
    if (Directory.EnumerateDirectories(workRoot).Any()) throw new Exception("Native session work files were not cleaned up.");
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        try
        {
            await using var unexpected = await MediaProcess.StartAsync(root, source, configuration, 1002, "DirectML", workRoot, worker, cancelled.Token);
            throw new Exception("A cancelled native open was accepted.");
        }
        catch (OperationCanceledException) { }
    }
    string invalidMedia = Path.Combine(output, "incomplete.mp4");
    File.WriteAllText(invalidMedia, "Incomplete media fixture");
    await using (var broken = await MediaProcess.StartAsync(root, invalidMedia, configuration, 1002, "DirectML", workRoot, worker, default))
    {
        var timeout = Stopwatch.StartNew();
        JsonObject state;
        do
        {
            state = await broken.GetStatusAsync(default);
            if (state["state"]?.GetValue<string>() == "failed") break;
            if (timeout.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Invalid media did not fail cleanly.");
            await Task.Delay(40);
        } while (true);
        evidence.Add(new() { ["invalidMedia"] = state });
    }
    if (Directory.EnumerateDirectories(workRoot).Any()) throw new Exception("Cancelled/failed native sessions retained work directories.");
    if (args.Length == 5) await NativeAddonChecks.RunAsync(root, output, worker, args[3], args[4], evidence);
    if (File.Exists(Path.Combine(root, "addon-host", "native-frames.json")))
        await NativeFrameChecks.RunAsync(root, output, worker, args.Length == 5 ? args[3] : null, args.Length == 5 ? args[4] : null, evidence);
    File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { passed = true, evidence }));
    Console.WriteLine("PASS native libmpv adapter and two independent supervised sessions: 2x DirectML, status, pause, seek, resume, EOF and cleanup.");
    return 0;
}
catch (Exception error)
{
    File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { passed = false, error = error.ToString(), evidence }));
    Console.Error.WriteLine(error); return 1;
}
