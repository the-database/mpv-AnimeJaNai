using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Native;

internal static class NativeEncodingChecks
{
    private const string Configuration = "[global]\nconfig_version=3\nbackend=DirectML\nlogging=yes\ndefault_slot=1002\n";
    public static async Task RunAsync(string root, string output, WorkerCommand command, List<JsonObject> evidence)
    {
        string work = Path.Combine(output, "workers");
        string source = await FixtureAsync(root, output);
        foreach (var options in new[] {
            new NativeEncoding("h264", "matroska", 4000, "aac", LengthSeconds: 2),
            new NativeEncoding("hevc", "mpegts", 4000, "aac", LengthSeconds: 2),
            new NativeEncoding("av1", "fragmentedMp4", 4000, "opus", LengthSeconds: 2),
        })
        {
            string name = options.VideoCodec + "-" + options.Container;
            string destination = Path.Combine(output, name + ".media");
            await using (var process = await MediaProcess.StartAsync(root, source, Configuration, 1002, "DirectML", work, command, default, encoding: options))
            {
                using (var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await process.EncodedOutput.CopyToAsync(file).WaitAsync(TimeSpan.FromSeconds(30));
                var final = await UntilAsync(process, s => s["state"]?.GetValue<string>() == "completed");
                Require(final["outputWidth"]?.GetValue<long>() == 960 && final["outputHeight"]?.GetValue<long>() == 720, "Native encoding must use actual 2x output.");
                Require(final["pixelFormat"]?.GetValue<string>() == "d3d11", "Encoding must receive the D3D11 frame, without CPU download.");
                evidence.Add(new() { ["nativeOutput"] = name, ["bytes"] = new FileInfo(destination).Length, ["status"] = final });
            }
            await VerifyAsync(root, output, name, destination, evidence);
        }
        // Concurrent encoded sessions must own separate descriptors and muxers.
        var pair = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => MediaProcess.StartAsync(root, source, Configuration, 1002, "DirectML", work, command, default,
            encoding: new("h264", "mpegts", 4000, "aac", LengthSeconds: 2))));
        try
        {
            var totals = await Task.WhenAll(pair.Select(async (session, index) =>
            {
                string path = Path.Combine(output, "concurrent-" + index + ".ts");
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await session.EncodedOutput.CopyToAsync(file).WaitAsync(TimeSpan.FromSeconds(30));
                await UntilAsync(session, s => s["state"]?.GetValue<string>() == "completed");
                return new FileInfo(path).Length;
            }));
            Require(totals.All(n => n > 100000), "Concurrent outputs were incomplete.");
            evidence.Add(new() { ["concurrentEncodedSessions"] = 2, ["firstBytes"] = totals[0], ["secondBytes"] = totals[1] });
        }
        finally { await Task.WhenAll(pair.Select(p => p.DisposeAsync().AsTask())); }

        // Loss of the trusted consumer must become an output failure, without
        // presenting a truncated stream as successful completion.
        await using (var disconnected = await MediaProcess.StartAsync(root, source, Configuration, 1002, "DirectML", work, command, default,
            encoding: new("h264", "matroska", 4000, "aac")))
        {
            byte[] first = new byte[2048];
            Require(await disconnected.EncodedOutput.ReadAsync(first).AsTask().WaitAsync(TimeSpan.FromSeconds(20)) > 0, "No initial output.");
            disconnected.EncodedOutput.Dispose();
            var failure = await UntilAsync(disconnected, s => s["state"]?.GetValue<string>() == "failed", allowFailure: true);
            evidence.Add(new() { ["disconnectedConsumer"] = failure });
        }

        // Both native contexts are running independently. Leave one output
        // completely unread, so its bounded OS pipe forces producer backpressure.
        await using (var blocked = await MediaProcess.StartAsync(root, source, Configuration, 1002, "DirectML", work, command, default,
            encoding: new("h264", "matroska", 4000, "aac")))
        await using (var observer = await MediaProcess.StartAsync(root, source, Configuration, 1002, "DirectML", work, command, default))
        {
            await UntilAsync(observer, s => s["positionSeconds"]?.GetValue<double>() > .5);
            await UntilAsync(blocked, s => s["outputWidth"]?.GetValue<long>() == 960);
            var first = await blocked.GetStatusAsync(default);
            double prior = (await observer.GetStatusAsync(default))["positionSeconds"]!.GetValue<double>();
            await Task.Delay(1200);
            var second = await blocked.GetStatusAsync(default);
            var progressed = await UntilAsync(observer, s => s["positionSeconds"]?.GetValue<double>() > prior + .8);
            Require(second["state"]?.GetValue<string>() != "completed", "An unread output must not buffer the entire source.");
            try { await blocked.SeekAsync(1, default); throw new Exception("Seeking a muxed stream should require a new session."); }
            catch (AddonException error) when (error.Code == "operation_unavailable") { }
            var closing = Stopwatch.StartNew();
            await blocked.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
            Require(closing.Elapsed < TimeSpan.FromSeconds(7), "Blocked output cleanup exceeded its deadline.");
            await UntilAsync(observer, s => s["positionSeconds"]?.GetValue<double>() > progressed["positionSeconds"]!.GetValue<double>() + .2);
            evidence.Add(new() { ["blockedOutput"] = new JsonObject { ["first"] = first, ["second"] = second,
                ["independentPlayback"] = progressed, ["closeMilliseconds"] = closing.Elapsed.TotalMilliseconds } });
        }
        Require(!Directory.EnumerateDirectories(work).Any(), "Encoded output workers retained temporary files.");
        evidence.Add(new() { ["cleanup"] = "all encoded and observing sessions released" });
        Console.WriteLine("PASS encoded native outputs, decoded video/audio, bounded backpressure and independent playback.");
    }

    private static async Task<JsonObject> UntilAsync(MediaProcess session, Func<JsonObject, bool> ready, bool allowFailure = false)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(25))
        {
            var state = await session.GetStatusAsync(default);
            Require(allowFailure || state["state"]?.GetValue<string>() != "failed", "Native output failed: " + state);
            if (ready(state)) return state;
            await Task.Delay(30);
        }
        throw new TimeoutException("Native output state deadline: " + await session.GetStatusAsync(default));
    }
    private static async Task<string> FixtureAsync(string root, string output)
    {
        string audio = Path.Combine(output, "sine.wav"), video = Path.Combine(output, "pattern-av.mkv");
        const int rate = 48000, samples = rate * 8, dataBytes = samples * 2;
        using (var writer = new BinaryWriter(File.Create(audio)))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + dataBytes); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2);
            writer.Write((short)2); writer.Write((short)16); writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(dataBytes);
            for (int i = 0; i < samples; i++) writer.Write((short)(12000 * Math.Sin(2 * Math.PI * 440 * i / rate)));
        }
        await RunAsync(root, output, "fixture", ["--hwdec=no", "--of=matroska", "--ovc=libx264", "--ovcopts=crf=18,preset=ultrafast",
            "--oac=pcm_s16le", "--audio-file=" + audio, "--o=" + video,
            "--vf=format=colormatrix=bt.709:primaries=bt.709:gamma=bt.1886", "av://lavfi:testsrc2=size=480x360:rate=24:duration=8"]);
        return video;
    }
    private static async Task VerifyAsync(string root, string output, string name, string source, List<JsonObject> evidence)
    {
        string checksums = Path.Combine(output, name + ".md5"), script = Path.Combine(output, name + ".lua");
        File.WriteAllText(script, "local mp=require 'mp'; local u=require 'mp.utils'; mp.observe_property('video-params','native',function(_,v) if v then print('AJN_ENCODED_INFO '..u.format_json(v)) end end)\n");
        string log = await RunAsync(root, output, name + "-decode", ["--hwdec=no", "--of=framemd5", "--ovc=rawvideo", "--oac=pcm_s16le",
            "--o=" + checksums, "--script=" + script, source]);
        var rows = File.ReadAllLines(checksums);
        var timebases = new Dictionary<int, double>();
        var types = new Dictionary<int, string>();
        foreach (var row in rows)
        {
            if (row.StartsWith("#tb "))
            {
                var parts = row[4..].Split(':', 2); var fraction = parts[1].Trim().Split('/');
                timebases[int.Parse(parts[0])] = double.Parse(fraction[0], CultureInfo.InvariantCulture) / double.Parse(fraction[1], CultureInfo.InvariantCulture);
            }
            if (row.StartsWith("#media_type ")) { var parts = row[12..].Split(':', 2); types[int.Parse(parts[0])] = parts[1].Trim(); }
        }
        Require(types.Values.Contains("video") && types.Values.Contains("audio"), "Both media tracks must survive encoding.");
        var tracks = rows.Where(r => r.Length > 0 && r[0] != '#').Select(r => r.Split(',').Select(v => v.Trim()).ToArray())
            .GroupBy(r => int.Parse(r[0])).ToDictionary(g => types[g.Key], g => new {
                Count = g.Count(), Start = g.Min(r => double.Parse(r[2], CultureInfo.InvariantCulture) * timebases[g.Key]),
                End = g.Max(r => (double.Parse(r[2], CultureInfo.InvariantCulture) + double.Parse(r[3], CultureInfo.InvariantCulture)) * timebases[g.Key]),
                Distinct = g.Select(r => r[^1]).Distinct().Count(),
            });
        Require(tracks["video"].Count == 48 && tracks["video"].Distinct == 48, "Expected 48 distinct decoded moving frames.");
        Require(Math.Abs(tracks["audio"].Start - tracks["video"].Start) < .15 && Math.Abs(tracks["audio"].End - tracks["video"].End) < .15,
            "Decoded audio/video timestamps drifted outside the fixture tolerance.");
        Require(Math.Abs(tracks["video"].End - tracks["video"].Start - 2) < .1, "Encoded video duration changed.");
        var metadata = log.Split('\n').Where(l => l.Contains("AJN_ENCODED_INFO ")).Select(l => JsonNode.Parse(l[(l.IndexOf("AJN_ENCODED_INFO ", StringComparison.Ordinal) + 17)..])!.AsObject()).ToArray();
        Require(metadata.Any(m => m["w"]?.GetValue<int>() == 960 && m["h"]?.GetValue<int>() == 720 &&
            m["primaries"]?.GetValue<string>() == "bt.709" && m["gamma"]?.GetValue<string>() == "bt.1886" && m["colormatrix"]?.GetValue<string>() == "bt.709"),
            "Encoded dimensions or color tags did not survive decoding.");
        evidence.Add(new() { ["decoded"] = name, ["videoFrames"] = tracks["video"].Count, ["audioFrames"] = tracks["audio"].Count,
            ["audioStart"] = tracks["audio"].Start, ["audioEnd"] = tracks["audio"].End, ["videoStart"] = tracks["video"].Start, ["videoEnd"] = tracks["video"].End,
            ["metadata"] = metadata.Last() });
    }
    private static async Task<string> RunAsync(string root, string output, string name, string[] args)
    {
        var info = new ProcessStartInfo(Path.Combine(root, "mpv.exe")) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = output };
        foreach (string arg in new[] { "--no-config", "--load-scripts=no", "--no-sub", "--input-terminal=no", "--terminal=yes", "--msg-level=all=info" }) info.ArgumentList.Add(arg);
        foreach (string nameOption in new[] { "osc", "ytdl", "load-stats-overlay", "load-console", "load-auto-profiles", "load-select", "load-positioning", "load-commands", "load-context-menu" }) info.ArgumentList.Add("--" + nameOption + "=no");
        foreach (string arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new Exception("Could not start native fixture process.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25)); }
        catch { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
        string log = await stdout + "\n" + await stderr; File.WriteAllText(Path.Combine(output, name + ".log"), log);
        Require(process.ExitCode == 0, "Native fixture process failed: " + name); return log;
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
