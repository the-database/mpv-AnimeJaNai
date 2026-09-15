using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Native;

// Independent FFmpeg decoding measures packet presentation times and content.
// This executable is a test dependency only, never an addon dependency.
internal static class NativeStreamTimingChecks
{
    private const string Configuration = "[global]\nconfig_version=3\nbackend=DirectML\nlogging=yes\ndefault_slot=1002\n";
    // Eight broad luminance cells encode the input frame number. Averaging each
    // upscaled cell tolerates lossy compression while detecting wrong preroll,
    // skipped frames and duplicated content independently of player timestamps.
    private const string FrameBarcode = "geq=lum='if(lt(Y,32)*lt(X,256),16+219*mod(floor(N/pow(2,floor(X/32))),2),lum(X,Y))':cb='if(lt(Y,16)*lt(X,128),128,cb(X,Y))':cr='if(lt(Y,16)*lt(X,128),128,cr(X,Y))'";
    internal static async Task EnduranceAsync(string root, string output, WorkerCommand command, string ffmpeg, List<JsonObject> evidence)
    {
        string pattern = await NativeEncodingChecks.FixtureAsync(root, output), episode = Path.Combine(output, "episode-22-minutes.mkv");
        await Ffmpeg(ffmpeg, output, "episode-fixture", ["-stream_loop", "164", "-i", pattern, "-t", "1320", "-c", "copy", episode]);
        // More than the production window keeps the encoder active throughout the pause.
        var pausePaths = await Produce(root, output, "five-minute-pause", episode, command, 0, 30, evidence, pauseSeconds: 300);
        int pauseFrames = 0;
        foreach (string path in pausePaths)
            pauseFrames += (await Decode(ffmpeg, Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path), path)).Count(f => f.Type == "video");
        Require(pauseFrames == 720, "Five-minute pause lost or duplicated video frames.");
        Console.WriteLine("PASS five-minute native pause/resume with all 720 video frames.");

        var timer = Stopwatch.StartNew();
        var paths = await Produce(root, output, "full-episode", episode, command, 0, 1320, evidence);
        double elapsed = timer.Elapsed.TotalSeconds;
        Require(elapsed <= 1320, "Episode processing did not sustain 1x on this machine/profile.");
        int frames = 0; double maximumSync = 0;
        foreach (string path in paths)
        {
            var decoded = await Decode(ffmpeg, Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path), path);
            var video = decoded.Where(f => f.Type == "video").ToArray(); var audio = decoded.Where(f => f.Type == "audio").ToArray();
            Require(video.Length > 0 && audio.Length > 0, "Episode segment lost a media track.");
            double sync = Math.Max(Math.Abs(video[0].Pts - audio[0].Pts), Math.Abs(video[^1].End - audio[^1].End));
            maximumSync = Math.Max(maximumSync, sync); Require(sync <= .1, "Episode A/V timing exceeded 100 ms.");
            frames += video.Length;
        }
        Require(frames == 31680, "Full episode lost or duplicated video frames: " + frames);
        evidence.Add(new() { ["episodeSeconds"] = 1320, ["videoFrames"] = frames, ["processingWallSeconds"] = elapsed,
            ["processingSpeed"] = 1320 / elapsed, ["maximumAvOffsetSeconds"] = maximumSync,
            ["profile"] = "Balanced slot 1002, DirectML SD model, synthetic 480x360 to 960x720 H264/AAC stereo", ["subtitles"] = "none" });
        Console.WriteLine("PASS full 22-minute native stream, independently decoded segments and A/V timing.");
    }
    internal static async Task RunAsync(string root, string output, WorkerCommand command, string ffmpeg, List<JsonObject> evidence)
    {
        foreach (var (label, rate) in new[] { ("24000-1001", 24000.0 / 1001), ("24", 24.0), ("25", 25.0), ("30000-1001", 30000.0 / 1001), ("30", 30.0), ("50", 50.0), ("60", 60.0) })
        {
            string source = Path.Combine(output, "source-" + label + ".mkv");
            await Ffmpeg(ffmpeg, output, "fixture-" + label, ["-f", "lavfi", "-i", "testsrc2=size=480x360:rate=" + label.Replace('-', '/') + ":duration=6",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=6", "-vf", FrameBarcode,
                "-c:v", "libx264", "-preset", "ultrafast", "-crf", "16", "-c:a", "pcm_s16le", source]);
            double start = 32 / rate, duration = 60 / rate;
            // Balanced's built-in chains stop at 31fps. Force the HD chain for
            // 50/60fps so these checks still exercise actual AJN processing.
            var paths = await Produce(root, output, "timing-" + label, source, command, start, duration, evidence, slot: rate > 31 ? 1010 : 1002);
            var frames = new List<Frame>();
            var frameNumbers = new List<int>();
            foreach (var path in paths)
            {
                var decoded = await Decode(ffmpeg, Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path), path);
                var video = decoded.Where(f => f.Type == "video").ToArray(); var audio = decoded.Where(f => f.Type == "audio").ToArray();
                Require(video.Length > 0 && audio.Length > 0, "An independent segment lost a media track.");
                Require(Math.Abs(video[0].Pts - audio[0].Pts) <= .1 && Math.Abs(video[^1].End - audio[^1].End) <= .1, "A/V timing exceeded 100 ms: " + label);
                frames.AddRange(video);
                frameNumbers.AddRange(await ReadBarcodes(ffmpeg, path));
            }
            Require(frames.Count == 60, "CFR seek or segmentation lost/duplicated frames: " + label + " = " + frames.Count);
            Require(frameNumbers.SequenceEqual(Enumerable.Range(32, 60)), "Decoded frame-number marks show incorrect seek content or segment boundaries: " + label + " = " + string.Join(',', frameNumbers));
            Require(frames.Select(f => f.Hash).Distinct().Count() == frames.Count, "CFR output duplicated image content: " + label);
            Require(Math.Abs(frames.Sum(f => f.End - f.Pts) - duration) <= 1 / rate, "CFR decoded durations changed: " + label);
            evidence.Add(new() { ["cfr"] = label, ["frames"] = frames.Count, ["sourceStart"] = start, ["duration"] = duration, ["independentAudioVideoToleranceSeconds"] = .1 });
            Console.WriteLine("PASS native stream CFR timing " + label);
        }
        // Nonuniform timestamps must survive without converting the fixture to CFR.
        string vfr = Path.Combine(output, "source-vfr.mkv");
        await Ffmpeg(ffmpeg, output, "fixture-vfr", ["-f", "lavfi", "-i", "testsrc2=size=480x360:rate=30:duration=6", "-f", "lavfi", "-i", "sine=sample_rate=48000:duration=6",
            "-vf", FrameBarcode + ",select='if(lt(t,3),not(mod(n,2)),1)'", "-fps_mode", "vfr", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "16", "-c:a", "pcm_s16le", vfr]);
        var vfrPaths = await Produce(root, output, "timing-vfr", vfr, command, 1, 4, evidence);
        var vfrFrames = new List<Frame>();
        var deltas = new List<double>();
        var vfrNumbers = new List<int>();
        foreach (var path in vfrPaths)
        {
            var video = (await Decode(ffmpeg, Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path), path)).Where(f => f.Type == "video").ToArray();
            vfrFrames.AddRange(video);
            vfrNumbers.AddRange(await ReadBarcodes(ffmpeg, path));
            for (int i = 1; i < video.Length; i++) deltas.Add(Math.Round(video[i].Pts - video[i - 1].Pts, 3));
        }
        Require(vfrFrames.Count == 90, "VFR frame count was changed: " + vfrFrames.Count);
        Require(vfrNumbers.SequenceEqual(Enumerable.Range(15, 30).Select(n => n * 2).Concat(Enumerable.Range(90, 60))), "VFR presented content differs from the selected source interval.");
        Require(deltas.Distinct().Count() > 1, "VFR output was silently flattened to one presentation interval.");
        evidence.Add(new() { ["vfrFrames"] = vfrFrames.Count, ["timestampBasedCheck"] = true });
    }

    internal static async Task<List<string>> Produce(string root, string output, string name, string source, WorkerCommand command,
        double start, double duration, List<JsonObject> evidence, OutputPlayback? playback = null, int pauseSeconds = 0,
        string? subtitleSource = null, RemoteInputPlan? remoteSubtitles = null, int slot = 1002)
    {
        string area = Path.Combine(output, name); Directory.CreateDirectory(area);
        await using var cache = StreamCache.Create(area, start); var index = new SegmentIndex("matroska");
        var stopwatch = Stopwatch.StartNew(); bool paused = false;
        var paths = new List<string>(); long copied = -1;
        double? firstSource = null, lastSource = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(90, duration * 3 + pauseSeconds)));
        async Task ReceiveCompleted()
        {
            await cache.PublishAsync(index.Read(cache.Directory), deadline.Token);
            var page = cache.List(cache.Generation + ":" + (copied + 1), 32);
            foreach (var segment in page["segments"]!.AsArray())
            {
                firstSource ??= segment!["sourceStartSeconds"]!.GetValue<double>();
                lastSource = segment!["sourceEndSeconds"]!.GetValue<double>();
                copied = segment!["sequence"]!.GetValue<long>(); string path = Path.Combine(area, "received-" + copied + ".mkv");
                using var destination = File.Create(path);
                using var lease = cache.Acquire(cache.Generation, segment["resourceId"]!.GetValue<string>());
                await lease.File.CopyToAsync(destination, deadline.Token); paths.Add(path);
            }
        }
        await using (var process = await MediaProcess.StartAsync(root, source, Configuration, slot, "DirectML", Path.Combine(area, "workers"), command, deadline.Token,
            encoding: new("h264", "matroska", 4000, "aac", LengthSeconds: duration, AudioChannels: 2), playback: playback ?? new(start),
            servedOutput: new(cache.Directory, "matroska", true, 1), subtitleSource: subtitleSource, remoteSubtitles: remoteSubtitles))
        {
            while (true)
            {
                var state = await process.GetStatusAsync(deadline.Token);
                Require(state["state"]?.GetValue<string>() != "failed", "Stream failed: " + state);
                await ReceiveCompleted();
                if (state["state"]?.GetValue<string>() == "completed")
                {
                    Require(state["outputWidth"]?.GetValue<long>() == 960 && state["outputHeight"]?.GetValue<long>() == 720, "Fixture did not undergo 2x AJN processing.");
                    evidence.Add(new() { ["stream"] = name, ["profileSlot"] = slot, ["elapsedSeconds"] = stopwatch.Elapsed.TotalSeconds, ["native"] = state, ["encodedMedia"] = cache.EncodedMetadata() }); break;
                }
                double produced = state["buffer"]?["producedEndSeconds"]?.GetValue<double>() ?? start;
                if (!paused && pauseSeconds > 0 && produced > start + 2)
                {
                    await process.PauseAsync(true, deadline.Token); paused = true;
                    var pause = Stopwatch.StartNew();
                    while (pause.Elapsed.TotalSeconds < pauseSeconds)
                    {
                        await Task.Delay(1000, deadline.Token);
                        var still = await process.GetStatusAsync(deadline.Token);
                        Require(still["state"]?.GetValue<string>() != "failed", "Intentional pause caused a transport failure: " + still);
                    }
                    await process.PauseAsync(false, deadline.Token);
                    evidence.Add(new() { ["pauseSeconds"] = pause.Elapsed.TotalSeconds, ["sameGeneration"] = cache.Generation });
                }
                if (produced > start) { await process.SetDemandAsync(Math.Max(start, produced - 2), deadline.Token); cache.Expire(produced - 2); }
                await Task.Delay(50, deadline.Token);
            }
        }
        await ReceiveCompleted(); cache.Complete(index.Read(cache.Directory));
        Require(paths.Count > 0, "No completed segments received.");
        Require(Math.Abs(firstSource!.Value - start) <= .05 && Math.Abs(lastSource!.Value - start - duration) <= .05,
            "Segment source interval disagrees with the requested playback range: " + firstSource + ".." + lastSource);
        return paths;
    }
    internal sealed record Frame(string Type, double Pts, double End, string Hash);
    private static async Task<int[]> ReadBarcodes(string ffmpeg, string source)
    {
        string path = Path.ChangeExtension(source, ".barcode"), directory = Path.GetDirectoryName(source)!;
        await Ffmpeg(ffmpeg, directory, Path.GetFileNameWithoutExtension(source) + "-barcode",
            ["-i", source, "-an", "-vf", "crop=512:64:0:0,scale=8:1:flags=area,format=gray", "-fps_mode", "passthrough", "-f", "rawvideo", path]);
        byte[] bytes = File.ReadAllBytes(path); Require(bytes.Length % 8 == 0, "Invalid decoded frame barcode.");
        return Enumerable.Range(0, bytes.Length / 8).Select(n => Enumerable.Range(0, 8).Sum(bit => bytes[n * 8 + bit] >= 128 ? 1 << bit : 0)).ToArray();
    }
    internal static async Task<Frame[]> Decode(string ffmpeg, string output, string name, string source)
    {
        string path = Path.Combine(output, name + "-decoded.md5");
        await Ffmpeg(ffmpeg, output, name + "-decode", ["-copyts", "-i", source, "-map", "0:v:0", "-map", "0:a:0", "-fps_mode", "passthrough", "-c:v", "rawvideo", "-c:a", "pcm_s16le", "-f", "framemd5", path]);
        var types = new Dictionary<int, string>(); var bases = new Dictionary<int, double>(); var result = new List<Frame>();
        foreach (string line in File.ReadLines(path))
        {
            if (line.StartsWith("#media_type ")) { var p = line[12..].Split(':', 2); types[int.Parse(p[0])] = p[1].Trim(); }
            else if (line.StartsWith("#tb ")) { var p = line[4..].Split(':', 2); var ratio = p[1].Trim().Split('/'); bases[int.Parse(p[0])] = double.Parse(ratio[0], CultureInfo.InvariantCulture) / double.Parse(ratio[1], CultureInfo.InvariantCulture); }
            else if (line.Length > 0 && line[0] != '#')
            {
                var p = line.Split(',').Select(s => s.Trim()).ToArray(); int track = int.Parse(p[0]);
                double pts = double.Parse(p[2], CultureInfo.InvariantCulture) * bases[track], duration = double.Parse(p[3], CultureInfo.InvariantCulture) * bases[track];
                result.Add(new(types[track], pts, pts + duration, p[^1]));
            }
        }
        return result.ToArray();
    }
    internal static async Task Ffmpeg(string executable, string output, string name, string[] args)
    {
        var info = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string arg in new[] { "-hide_banner", "-nostdin", "-y" }.Concat(args)) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!; Task<string> stderr = process.StandardError.ReadToEndAsync(), stdout = process.StandardOutput.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5)); }
        catch { process.Kill(true); await process.WaitForExitAsync(); throw; }
        File.WriteAllText(Path.Combine(output, name + ".log"), await stdout + "\n" + await stderr);
        Require(process.ExitCode == 0, "FFmpeg test command failed: " + name);
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
