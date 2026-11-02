using System.Diagnostics;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Native;

internal static class NativeFrameChecks
{
    public static async Task RunAsync(string root, string output, WorkerCommand command, string? runtime, string? compiler, List<JsonObject> evidence)
    {
        string area = Path.Combine(output, "frames"); Directory.CreateDirectory(area);
        string sourceVideo = await CreatePatternAsync(root, area);
        string config = Path.Combine(area, "animejanai.conf");
        File.WriteAllText(config, "[global]\nconfig_version=3\nbackend=DirectML\nlogging=no\ndefault_slot=1002\n");
        using (var buffer = new NativeFrameBuffer())
        using (var player = new NativePlayback(root, sourceVideo, config, area, 1002, "DirectML", buffer.Handle))
        {
            using var subscription = buffer.Subscribe(new(64, 36, 30));
            FramePacket Until(Func<FramePacket, bool> ready, IFrameSubscription? reader = null)
            {
                var clock = Stopwatch.StartNew();
                FramePacket? last = null;
                while (clock.Elapsed < TimeSpan.FromSeconds(25))
                {
                    var status = player.Poll();
                    if (player.Failed) throw new Exception("Sample playback failed: " + status);
                    var frame = (reader ?? subscription).ReadLatest();
                    if (frame is not null) last = frame;
                    if (frame is not null && ready(frame)) return frame;
                }
                throw new TimeoutException("Native sample did not become ready. Last: " + last?.Metadata + "; distinct bytes=" + last?.Pixels.ToArray().Distinct().Count());
            }
            var first = Until(_ => true);
            evidence.Add(new() { ["firstNativeSample"] = first.Metadata, ["distinctBytes"] = first.Pixels.ToArray().Distinct().Count() });
            // Observe actual picture content rather than accepting a correctly
            // sized all-black image. The normal AJN benchmark is itself black.
            if (first.Pixels.ToArray().Distinct().Count() < 10)
                first = Until(frame => frame.Pixels.ToArray().Distinct().Count() >= 10);
            if (first.Pixels.Length != 64 * 36 * 4 || first.Metadata["sourceWidth"]!.GetValue<int>() != 960 || first.Metadata["sourceHeight"]!.GetValue<int>() != 720 ||
                first.Metadata["color"]!["primaries"]!.GetValue<string>() != "bt.709" || first.Pixels.Span.ToArray().Distinct().Count() < 10)
                throw new Exception("Expected a small, nonempty SDR sample from actual 2x inference output.");
            File.WriteAllBytes(Path.Combine(area, "sample.bgra"), first.Pixels.ToArray());
            File.WriteAllText(Path.Combine(area, "sample.json"), first.Metadata.ToJsonString());
            var slow = Stopwatch.StartNew();
            while (slow.Elapsed < TimeSpan.FromMilliseconds(350)) player.Poll();
            var latest = Until(_ => true);
            if (latest.Metadata["skippedSamples"]!.GetValue<ulong>() < 2 || latest.Metadata["ptsSeconds"]!.GetValue<double>() <= first.Metadata["ptsSeconds"]!.GetValue<double>())
                throw new Exception("A slow consumer did not receive the latest sample with a bounded drop count.");
            string epoch = first.Metadata["epoch"]!.GetValue<string>();
            buffer.InvalidateForSeek(); player.Seek(10);
            var sought = Until(frame => frame.Metadata["epoch"]!.GetValue<string>() != epoch && frame.Metadata["ptsSeconds"]!.GetValue<double>() >= 9.9);
            subscription.Dispose();
            using var maximum = buffer.Subscribe(new(320, 180, 60));
            var large = Until(frame => frame.Pixels.Length == FrameRequest.MaxBytes, maximum);
            evidence.Add(new() { ["nativeSample"] = first.Metadata, ["slowConsumer"] = latest.Metadata, ["afterSeek"] = sought.Metadata,
                ["maximumSample"] = large.Metadata, ["binaryBytes"] = first.Pixels.Length });
        }
        Console.WriteLine("PASS real 2x DirectML sample: GPU reduction, binary pixels, geometry/color/PTS and seek epoch.");
        string hdr = await CreatePatternAsync(root, area, hdr: true);
        using (var buffer = new NativeFrameBuffer())
        using (var player = new NativePlayback(root, hdr, config, area, 1002, "DirectML", buffer.Handle))
        using (var subscription = buffer.Subscribe(new()))
        {
            var clock = Stopwatch.StartNew(); bool rejected = false; JsonObject state = new();
            while (clock.Elapsed < TimeSpan.FromSeconds(15))
            {
                state = player.Poll();
                if (player.Failed) throw new Exception("An unsupported sample stopped native processing: " + state);
                try { _ = subscription.ReadLatest(); }
                catch (AddonException error) when (error.Code == "frame_format_unavailable") { rejected = true; }
                if (rejected && state["outputWidth"]?.GetValue<long>() == 960) break;
            }
            if (!rejected || state["outputWidth"]?.GetValue<long>() != 960) throw new Exception("HDR sampling was not explicitly unavailable while video processing continued.");
            evidence.Add(new() { ["hdrSampleUnavailable"] = true, ["nativeVideoContinues"] = state });
        }
        Console.WriteLine("PASS HDR-tagged input reports sample unavailable while native video processing continues.");
        if (runtime is null || compiler is null) return;

        string sourceDirectory = Path.Combine(area, "source");
        DeveloperTools.New(sourceDirectory, "org.animejanai.frame-test");
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(sourceDirectory, "manifest.json")))!;
        manifest["api"]!["minMinor"] = 2;
        manifest["permissions"] = new JsonArray("sessions.manage", "frames.read");
        manifest["requiredCapabilities"] = new JsonObject {
            ["frames"] = new JsonObject { ["major"] = 1, ["minMinor"] = 0 },
            ["timers"] = new JsonObject { ["major"] = 1, ["minMinor"] = 0 },
        };
        File.WriteAllText(Path.Combine(sourceDirectory, "manifest.json"), manifest.ToJsonString());
        File.WriteAllText(Path.Combine(sourceDirectory, "addon.js"), """
            let sessions = [], subscriptions = [], counts = [0,0], last = [null,null];
            function onEvent(event, ajn) {
                if (event.name === "start") {
                    const selected = ajn.sessions.selections();
                    for (let i=0;i<2;i++) {
                        sessions.push(ajn.sessions.open(selected.sources[0].id,selected.profiles[0].id).sessionId);
                        subscriptions.push(ajn.frames.subscribe(sessions[i], {width:64,height:36,maxFps:30}).subscriptionId);
                    }
                    ajn.timers.set("samples", 33);
                }
                if (event.name === "timer") {
                    for (let i=0;i<subscriptions.length;i++) {
                        if (!subscriptions[i]) continue;
                        const frame = ajn.frames.read(subscriptions[i]);
                        if (!frame) continue;
                        if (frame.sourceWidth!==960 || frame.pixels.length!==9216 || frame.color.primaries!=="bt.709") throw Error("bad native sample");
                        let sum=0; for (let j=0;j<frame.pixels.length;j++) sum+=frame.pixels[j];
                        counts[i]++; last[i]={pts:frame.ptsSeconds,epoch:frame.epoch,checksum:sum};
                    }
                }
                if (event.name === "status") return { counts, last, status:sessions.map(id=>ajn.sessions.status(id)) };
                if (event.name === "pause") ajn.sessions.pause(sessions[0],true);
                if (event.name === "seek") ajn.sessions.seek(sessions[0],10);
                if (event.name === "unsubscribe") { ajn.frames.unsubscribe(subscriptions[0]); subscriptions[0]=null; }
            }
            """);
        var package = await DeveloperTools.BuildAsync(sourceDirectory, compiler, Path.Combine(area, "frame-test.ajnaddon"));
        var grant = new PermissionGrant(package, package.Manifest.Permissions);
        var selections = new MediaSelections(area);
        selections.ApproveSource(package, grant, sourceVideo);
        selections.ApproveProfile(package, grant, "2x DirectML", 1002, "DirectML", File.ReadAllText(config));
        var registry = new SessionRegistry(new NativeSessionProvider(root, area, selections, command), perOwnerLimit: 16);
        await using (var addon = await AddonWorker.StartAsync(package, grant, runtime, Path.Combine(area, "workers"), area, command, sessions: registry))
        {
            await addon.SendEventAsync("start");
            async Task<JsonObject> Until(Func<JsonObject, bool> ready)
            {
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed < TimeSpan.FromSeconds(25))
                {
                    var state = (JsonObject)(await addon.SendEventAsync("status"))!;
                    if (((JsonArray)state["status"]!).Any(s => s?["state"]?.GetValue<string>() == "failed")) throw new Exception("Frame session failed: " + state);
                    if (ready(state)) return state;
                    await Task.Delay(50);
                }
                throw new TimeoutException("Wasm frame consumer did not become ready.");
            }
            var initial = await Until(s => s["counts"]![0]!.GetValue<int>() >= 5 && s["counts"]![1]!.GetValue<int>() >= 5);
            await addon.SendEventAsync("pause");
            await Until(s => s["status"]![0]!["paused"]?.GetValue<bool>() == true);
            await Task.Delay(150); var paused = await Until(_ => true);
            int count = paused["counts"]![0]!.GetValue<int>(), other = paused["counts"]![1]!.GetValue<int>();
            var independent = await Until(s => s["counts"]![1]!.GetValue<int>() >= other + 4);
            if (independent["counts"]![0]!.GetValue<int>() != count) throw new Exception("Paused session continued producing samples.");
            string epoch = independent["last"]![0]!["epoch"]!.GetValue<string>();
            await addon.SendEventAsync("seek");
            var sought = await Until(s => s["last"]![0]!["epoch"]!.GetValue<string>() != epoch && s["last"]![0]!["pts"]!.GetValue<double>() >= 9.9);
            await addon.SendEventAsync("unsubscribe");
            int unsubscribed = sought["counts"]![0]!.GetValue<int>(); other = sought["counts"]![1]!.GetValue<int>();
            var active = await Until(s => s["counts"]![1]!.GetValue<int>() > other + 3);
            if (active["counts"]![0]!.GetValue<int>() != unsubscribed) throw new Exception("Unsubscribed consumer received more frames.");
            evidence.Add(new() { ["wasmFramesInitial"] = initial, ["independentPause"] = independent, ["afterSeek"] = sought, ["afterUnsubscribe"] = active });
            // Dispose the addon with the second subscription still active. The
            // host must stop timers, disable capture and drain both processes.
        }
        if (Directory.EnumerateDirectories(Path.Combine(area, "media-workers")).Any() || Directory.EnumerateDirectories(Path.Combine(area, "workers")).Any())
            throw new Exception("Frame consumer teardown left owned workers behind.");
        Console.WriteLine("PASS actual Wasm + timers + binary samples from two independent 2x GPU sessions, pause/seek/unsubscribe and active-consumer cleanup.");
        await InspectorAsync(root, area, sourceVideo, command, runtime, compiler, evidence);
    }

    private static async Task InspectorAsync(string root, string area, string source, WorkerCommand command, string runtime, string compiler, List<JsonObject> evidence)
    {
        string data = Path.Combine(area, "inspector"); Directory.CreateDirectory(data);
        var package = await DeveloperTools.BuildAsync(Path.Combine(AppContext.BaseDirectory, "sample-inspector"), compiler, Path.Combine(data, "sample-inspector.ajnaddon"));
        var grant = new PermissionGrant(package, package.Manifest.Permissions);
        var settings = new AddonSettings(data, package.Manifest); settings.Update(new() { ["sessionCount"] = 2 });
        var selections = new MediaSelections(data);
        selections.ApproveSource(package, grant, source);
        selections.ApproveProfile(package, grant, "2x DirectML", 1002, "DirectML", "[global]\nconfig_version=3\nbackend=DirectML\n");
        var sessions = new SessionRegistry(new NativeSessionProvider(root, data, selections, command), perOwnerLimit: 16);
        await using (var activation = new AddonActivation(package, async token => await AddonWorker.StartAsync(package, grant, runtime,
            Path.Combine(data, "workers"), data, command, sessions: sessions, cancellationToken: token)))
        {
            await activation.AcquireAsync("manual", "test");
            var opened = await activation.RunActionAsync("open");
            if (opened?["opened"]?.GetValue<int>() != 2) throw new Exception("Inspector did not open two sample sessions: " + opened);
            async Task<JsonArray> Ready(int count, int width)
            {
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed < TimeSpan.FromSeconds(20))
                {
                    var values = (JsonArray)(await activation.RunActionAsync("status"))!;
                    if (values.Count == count && values.All(s => s?["received"]?.GetValue<int>() >= 3 && s["last"]?["sample"]?[0]?.GetValue<int>() == width)) return values;
                    if (values.Any(s => s?["error"] is not null)) throw new Exception("Inspector sample failed: " + values);
                    await Task.Delay(60);
                }
                throw new TimeoutException("Inspector did not report expected samples.");
            }
            var small = await Ready(2, 64);
            await activation.UpdateSettingsAsync(settings, new() { ["sessionCount"] = 1, ["width"] = 320, ["height"] = 180 });
            await activation.RunActionAsync("close");
            var closeClock = Stopwatch.StartNew();
            while (Directory.EnumerateDirectories(Path.Combine(data, "media-workers")).Any())
            {
                if (closeClock.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Inspector sessions did not release capacity.");
                await Task.Delay(30);
            }
            opened = await activation.RunActionAsync("open");
            if (opened?["opened"]?.GetValue<int>() != 1) throw new Exception("Inspector could not reuse capacity: " + opened);
            var large = await Ready(1, 320);
            if (large[0]!["last"]!["binaryBytes"]!.GetValue<int>() != FrameRequest.MaxBytes) throw new Exception("Inspector did not receive maximum binary payload.");
            evidence.Add(new() { ["inspectorTwoSessions"] = small, ["inspectorChangedSettings"] = large });
        }
        if (Directory.EnumerateDirectories(Path.Combine(data, "media-workers")).Any()) throw new Exception("Inspector left native workers behind.");
        Console.WriteLine("PASS shipped sample-inspector source: two sessions, metadata actions, changed settings, maximum binary sample and cleanup.");
    }

    internal static async Task<string> CreatePatternAsync(string root, string area, bool hdr = false)
    {
        string destination = Path.Combine(area, hdr ? "pattern-hdr.mkv" : "pattern.mkv");
        var info = new ProcessStartInfo(Path.Combine(root, "mpv.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = area };
        foreach (string arg in new[] { "--no-config", "--load-scripts=no", "--no-audio", "--no-sub", "--input-terminal=no", "--terminal=yes", "--msg-level=all=warn",
            "--hwdec=no", "--of=matroska", "--ovc=libx264",
            hdr ? "--ovcopts=crf=18,preset=ultrafast,color_primaries=bt2020,color_trc=smpte2084" : "--ovcopts=crf=18,preset=ultrafast,color_primaries=bt709,color_trc=bt709",
            hdr ? "--vf=format=primaries=bt.2020:gamma=pq" : "--vf=format=primaries=bt.709:gamma=bt.1886", "--o=" + destination,
            "av://lavfi:testsrc2=size=480x360:rate=24:duration=40" }) info.ArgumentList.Add(arg);
        foreach (string name in new[] { "osc", "ytdl", "load-stats-overlay", "load-console", "load-auto-profiles", "load-select", "load-positioning", "load-commands", "load-context-menu" })
            info.ArgumentList.Add("--" + name + "=no");
        using var process = Process.Start(info) ?? throw new Exception("Could not create synthetic video fixture.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
        File.WriteAllText(Path.Combine(area, hdr ? "fixture-hdr.log" : "fixture.log"), await stdout + "\n" + await stderr);
        if (process.ExitCode != 0 || !File.Exists(destination)) throw new Exception("Synthetic video fixture failed; see fixture.log.");
        return destination;
    }
}
