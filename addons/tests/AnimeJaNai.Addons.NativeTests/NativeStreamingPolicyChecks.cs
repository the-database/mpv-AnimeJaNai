using System.Globalization;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Native;
using AnimeJaNai.Addons.TestSupport;

internal static class NativeStreamingPolicyChecks
{
    internal static async Task RunAsync(string root, string output, WorkerCommand command, string runtime, string compiler, string ffmpeg, List<JsonObject> evidence)
    {
        string fixture = Path.Combine(output, "hd-source.mkv"), ass = Path.Combine(output, "caption.ass");
        File.WriteAllText(ass, "[Script Info]\nScriptType: v4.00+\nPlayResX: 1920\nPlayResY: 1080\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\nStyle: Default,Arial,56,&H00FFFFFF,&H00FFFFFF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,3,0,2,20,20,80,1\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:00.00,0:00:12.00,Default,,0,0,0,,AJN native streaming subtitle test\n");
        await NativeStreamTimingChecks.Ffmpeg(ffmpeg, output, "fixture", ["-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=24", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-i", ass,
            "-t", "12", "-map", "0:v", "-map", "1:a", "-map", "2:s", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-c:s", "ass", fixture]);
        long length = new FileInfo(fixture).Length; int reads = 0;
        await using var upstream = new HttpFixture((request, _) =>
        {
            Require(request.Headers.GetValueOrDefault("X-Source-Test") == "stream-policy", "Wrong scoped fixture credential.");
            Interlocked.Increment(ref reads);
            long start = long.Parse(request.Headers["Range"][6..^1], CultureInfo.InvariantCulture);
            return Task.FromResult(new HttpReply(206, [], $"Content-Range: bytes {start}-{length - 1}/{length}\r\nETag: \"policy-fixture\"\r\nAccept-Ranges: bytes\r\n", FileBody: fixture, FileOffset: start));
        });
        string addon = Path.Combine(output, "addon"), data = Path.Combine(output, "managed");
        DeveloperTools.New(addon, "org.example.streaming-policy");
        File.WriteAllText(Path.Combine(addon, "manifest.json"), """
            {"schemaVersion":1,"id":"org.example.streaming-policy","name":"Streaming policy test","version":"1.0.0",
             "api":{"major":1,"minMinor":9},"permissions":["sessions.manage","media.input","media.output","network.connect","credentials.use"],
             "requiredCapabilities":{"mediaStreams":{"major":1,"minMinor":1}},"activation":["manual"]}
            """);
        File.WriteAllText(Path.Combine(addon, "addon.js"), """
            let handle = null, probe = null;
            function onEvent(e, ajn) {
                if (e.name === "probe") { probe = ajn.mediaProbe.open(e.data).probeId; return {}; }
                if (e.name === "probeStatus") { const s = ajn.mediaProbe.status(probe); return s.state === "completed" ? ajn.mediaProbe.result(probe) : s; }
                if (e.name === "probeClose") { ajn.mediaProbe.close(probe); return {}; }
                if (["check", "prepare", "open"].includes(e.name)) { handle = ajn.mediaStreams[e.name](e.data.source, e.data.profile, e.data.options); return handle; }
                if (e.name === "status") return ajn.mediaStreams.status(handle.streamId);
                if (e.name === "close") { ajn.mediaStreams.requestClose(handle.streamId); return {}; }
            }
            """);
        var package = await DeveloperTools.BuildAsync(addon, compiler, Path.Combine(output, "streaming-policy.ajnaddon"));
        var grant = new PermissionGrant(package, package.Manifest.Permissions);
        var media = new MediaSelections(data); var network = new NetworkSelections(data);
        string destination = network.Approve(package, grant, "Scoped fixture", await NetworkDestination.InspectAsync($"http://127.0.0.1:{upstream.Port}", default));
        network.SetCredential(package, grant, destination, "X-Source-Test", "stream-policy");
        var source = new JsonObject { ["type"] = "http", ["destinationId"] = destination, ["path"] = "/media", ["useCredential"] = true };
        var provider = new NativeSessionProvider(root, data, media, command, maximumSessions: 1, networkSelections: network);
        await using var worker = await AddonWorker.StartAsync(package, grant, runtime, Path.Combine(output, "workers"), data, command,
            sessions: new SessionRegistry(provider), networkSelections: network);
        Task<JsonNode?> Call(string name, JsonObject? value = null) => worker.SendEventAsync(name, value);
        async Task<JsonObject> Wait(Func<JsonObject, bool> done, bool allowFailure = false, int seconds = 1200)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            string? previous = null;
            while (true)
            {
                var state = (JsonObject)(await worker.SendEventAsync("status", cancellationToken: timeout.Token))!;
                string progress = state["operation"] + ":" + state["state"] + ":" + state["native"]?["processing"]?["state"];
                if (progress != previous) { Console.WriteLine(progress); previous = progress; File.WriteAllText(Path.Combine(output, "latest-status.json"), state.ToJsonString()); }
                if (!allowFailure) Require(state["error"] is null, state.ToJsonString());
                if (done(state)) return state;
                await Task.Delay(150, timeout.Token);
            }
        }
        async Task Close() { await Call("close"); await Wait(s => s["nativeCapacityReleased"]?.GetValue<bool>() == true && s["state"]?.GetValue<string>() is "closed" or "failed", true, 30); }
        await Call("probe", source);
        JsonObject probe;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
        {
            while (true)
            {
                probe = (JsonObject)(await worker.SendEventAsync("probeStatus", cancellationToken: timeout.Token))!;
                if (probe["tracks"] is JsonArray) break;
                Require(probe["state"]?.GetValue<string>() != "failed", probe.ToJsonString()); await Task.Delay(100, timeout.Token);
            }
        }
        string subtitle = probe["tracks"]!.AsArray().First(t => t?["type"]?.GetValue<string>() == "subtitle")!["trackId"]!.GetValue<string>();
        await Call("probeClose");
        string? testedModel = null;
        int testedSlot = 1002;
        foreach (string backend in new[] { "DirectML", "TensorRT" })
        {
            string? selectedBackend = Environment.GetEnvironmentVariable("AJN_STREAM_TEST_BACKEND");
            if (selectedBackend is not null && selectedBackend != backend) continue;
            string profile = media.ApproveProfile(package, grant, backend, backend == "DirectML" ? 1002 : 1003, backend, "[global]\nconfig_version=3\n");
            var request = new JsonObject { ["source"] = source.DeepClone(), ["profile"] = profile,
                ["options"] = new JsonObject { ["mode"] = "segments", ["segmentSeconds"] = 1,
                    ["encoding"] = new NativeEncoding("h264", "mpegts", 10000, "aac", 128, LengthSeconds: 8, AudioChannels: 2, Encoder: "nvenc").ToJson(),
                    ["playback"] = new JsonObject { ["subtitles"] = new JsonObject { ["mode"] = "burn", ["trackId"] = subtitle } } } };
            await Call("check", request);
            var checkedState = await Wait(s => s["ready"]?.GetValue<bool>() == true || s["state"]?.GetValue<string>() == "failed", true);
            evidence.Add(new() { ["operation"] = "check", ["backend"] = backend, ["status"] = checkedState.DeepClone() });
            if (checkedState["error"] is not null)
            {
                Require(backend == "TensorRT" && checkedState["error"]?["code"]?.GetValue<string>() is "engine_missing" or "engine_incompatible", checkedState.ToJsonString());
                await Close();
                // GPU driver allocations can outlive the worker briefly. This
                // fixture has a small system commit margin between operations.
                await Task.Delay(3000);
                await Call("prepare", request);
                var building = await Wait(s => s["native"]?["processing"]?["state"]?.GetValue<string>() == "building" || s["ready"]?.GetValue<bool>() == true);
                if (building["ready"]?.GetValue<bool>() != true)
                {
                    await Close(); evidence.Add(new() { ["operation"] = "cancel-building", ["backend"] = backend, ["passed"] = true });
                    await Call("prepare", request);
                }
                var prepared = await Wait(s => s["ready"]?.GetValue<bool>() == true);
                evidence.Add(new() { ["operation"] = "prepare", ["backend"] = backend, ["status"] = prepared.DeepClone() });
                await Close(); await Call("check", request);
                await Wait(s => s["ready"]?.GetValue<bool>() == true);
            }
            await Close(); await Call("open", request);
            var completed = await Wait(s => s["state"]?.GetValue<string>() == "producerCompleted");
            var video = completed["encodedMedia"]?["tracks"]?.AsArray().FirstOrDefault(t => t?["type"]?.GetValue<string>() == "video");
            Require(video?["width"]?.GetValue<long>() == 3840 && video["height"]!.GetValue<long>() == 2160, "Expected actual 4K output: " + completed);
            Require(video!["pixelFormat"]?.GetValue<string>() == "yuv420p", "Expected 8-bit H.264 output.");
            Require(completed["native"]?["processing"]?["actualBackend"]?.GetValue<string>() == backend, "Backend changed.");
            testedModel = completed["native"]?["processing"]?["activeModels"]?[0]?.GetValue<string>();
            testedSlot = backend == "TensorRT" ? 1003 : 1002;
            Require(completed["measurements"]?["processingMediaSecondsPerWallSecond"] is not null, "Final speed is missing.");
            string decoded = Path.Combine(output, backend); Directory.CreateDirectory(decoded);
            var segments = Directory.GetFiles(data, "part_*.ts", SearchOption.AllDirectories).Order().ToArray(); Require(segments.Length >= 7, "Missing segments.");
            foreach (string path in segments)
            {
                string copy = Path.Combine(decoded, Path.GetFileName(path)); File.Copy(path, copy);
                await NativeStreamTimingChecks.Ffmpeg(ffmpeg, decoded, Path.GetFileNameWithoutExtension(path) + "-decode", ["-v", "error", "-xerror", "-i", copy, "-map", "0:v", "-map", "0:a", "-f", "null", "-"]);
            }
            await NativeStreamTimingChecks.Ffmpeg(ffmpeg, decoded, "caption", ["-i", Path.Combine(decoded, Path.GetFileName(segments[1])), "-frames:v", "1", "-vf", "scale=960:540", Path.Combine(decoded, "caption.png")]);
            evidence.Add(new() { ["operation"] = "encoded-stream", ["backend"] = backend, ["decodedSegments"] = segments.Length, ["status"] = completed.DeepClone() });
            File.WriteAllText(Path.Combine(output, "progress.json"), new JsonArray(evidence.Select(e => (JsonNode?)e.DeepClone()).ToArray()).ToJsonString());
            await Close();
        }
        Require(testedModel is not null, "No active model identity was reported.");
        // Corrupt only a model in the owned, isolated test installation. Restore
        // it even on assertion failure. The original release stays untouched.
        string model = Path.Combine(root, "animejanai", "onnx", testedModel + ".onnx");
        Require(File.Exists(model), "Reported model is not in the isolated model directory.");
        string backup = Path.Combine(output, "model-backup.onnx"); File.Copy(model, backup);
        try
        {
            File.WriteAllBytes(model, [0]);
            string failedProfile = media.ApproveProfile(package, grant, "Broken model test", testedSlot, "DirectML", "[global]\nconfig_version=3\n");
            await Call("open", new() { ["source"] = source.DeepClone(), ["profile"] = failedProfile,
                ["options"] = new JsonObject { ["encoding"] = new NativeEncoding("h264", "mpegts", 1000, LengthSeconds: 2).ToJson() } });
            var failed = await Wait(s => s["state"]?.GetValue<string>() == "failed", true, 60);
            Require(failed["error"]?["code"]?.GetValue<string>() == "ai_filter_failed", failed.ToJsonString());
            Require(failed["encodedMedia"] is null, "A broken AI filter published apparent-success video.");
            evidence.Add(new() { ["operation"] = "broken-model", ["status"] = failed.DeepClone() });
            await Close();
        }
        finally { File.Copy(backup, model, true); }
        Require(reads > 0, "Remote path was not used.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
