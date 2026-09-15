using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Native;

internal static class NativeStreamingChecks
{
    private const string Configuration = "[global]\nconfig_version=3\nbackend=DirectML\nlogging=yes\ndefault_slot=1002\n";
    internal static async Task RunAsync(string root, string output, WorkerCommand command, List<JsonObject> evidence)
    {
        string work = Path.Combine(output, "workers");
        string source = await NativeEncodingChecks.FixtureAsync(root, output);
        var probe = await NativeProbeProcess.RunAsync(root, source, null, work, command, default);
        Require(probe["tracks"]!.AsArray().Any(t => t!["type"]!.GetValue<string>() == "audio"), "Probe lost fixture audio.");
        Require(probe["durationSeconds"]!.GetValue<double>() >= 7.9, "Probe duration changed.");
        evidence.Add(new() { ["probe"] = probe });

        string subtitles = Path.Combine(output, "selected.srt");
        File.WriteAllText(subtitles, "1\n00:00:00,200 --> 00:00:01,500\nSelected subtitle 日本語\n\n", new UTF8Encoding(false));
        byte[] webvtt = await NativeProbeProcess.RunPayloadAsync(root, subtitles, null, work, command, default, new(null, .5, 1.2));
        string text = Encoding.UTF8.GetString(webvtt);
        Require(text.StartsWith("WEBVTT", StringComparison.Ordinal) && text.Contains("00:00:00.500 --> 00:00:01.200") && text.Contains("日本語"), "WebVTT selection, clipping or Unicode changed: " + text);
        File.WriteAllBytes(Path.Combine(output, "selected.vtt"), webvtt);
        evidence.Add(new() { ["subtitleExtraction"] = "SRT to WebVTT", ["bytes"] = webvtt.Length, ["sourceTimeline"] = true });

        foreach (string container in new[] { "matroska", "mpegts", "fragmentedMp4" })
        foreach (bool segmented in new[] { false, true })
        {
            string name = container + (segmented ? "-segments" : "-continuous");
            await using var cache = StreamCache.Create(Path.Combine(output, name), 1);
            var index = new SegmentIndex(container);
            var encoding = new NativeEncoding("h264", container, 4000, "aac", LengthSeconds: 2, AudioChannels: 2);
            await using (var process = await MediaProcess.StartAsync(root, source, Configuration, 1002, "DirectML", work, command, default,
                encoding: encoding, playback: new(1), servedOutput: new(cache.Directory, container, segmented, 1)))
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                JsonObject status;
                do
                {
                    status = await process.GetStatusAsync(deadline.Token);
                    Require(status["state"]?.GetValue<string>() != "failed", name + ": " + status);
                    if (segmented) await cache.PublishAsync(index.Read(cache.Directory), deadline.Token); else cache.PublishContinuous(container);
                    if (status["state"]?.GetValue<string>() != "completed") await Task.Delay(50, deadline.Token);
                } while (status["state"]?.GetValue<string>() != "completed");
                Require(status["outputWidth"]?.GetValue<long>() == 960 && status["outputHeight"]?.GetValue<long>() == 720, "Stream did not upscale 2x.");
                evidence.Add(new() { ["nativeStream"] = name, ["status"] = status });
            }
            if (segmented) await cache.PublishAsync(index.Read(cache.Directory), default); else cache.PublishContinuous(container);
            cache.Complete(segmented ? index.Read(cache.Directory) : null);
            var page = cache.List(null, 32); var files = new List<string>();
            if (segmented)
            {
                Require(page["segments"]!.AsArray().Count >= 2, "Stream did not create independent one-second segments: " + name);
                foreach (var segment in page["segments"]!.AsArray())
                {
                    string target = Path.Combine(output, name + "-" + segment!["sequence"] + ".media");
                    using var destination = File.Create(target);
                    if (page["initializationId"] is JsonValue init)
                    { using var lease = cache.Acquire(cache.Generation, init.GetValue<string>()); await lease.File.CopyToAsync(destination); }
                    using (var lease = cache.Acquire(cache.Generation, segment["resourceId"]!.GetValue<string>())) await lease.File.CopyToAsync(destination);
                    files.Add(target);
                }
                evidence.Add(new() { ["segments"] = page });
            }
            else
            {
                string target = Path.Combine(output, name + ".media");
                using var destination = File.Create(target);
                using var lease = cache.Acquire(cache.Generation, page["continuousResourceId"]!.GetValue<string>());
                await lease.File.CopyToAsync(destination); files.Add(target);
            }
            int frames = 0;
            foreach (string file in files)
            {
                var decoded = await DecodeAsync(root, output, Path.GetFileNameWithoutExtension(file), file);
                Require(decoded.Video > 0 && decoded.Audio > 0, "Independent object lost video or audio: " + file);
                frames += decoded.Video;
                evidence.Add(new() { ["decodedObject"] = Path.GetFileName(file), ["videoFrames"] = decoded.Video, ["audioFrames"] = decoded.Audio });
            }
            Require(frames == 48, "Two-second 24fps output lost or duplicated video frames: " + name + " = " + frames);
        }
        Require(!Directory.EnumerateDirectories(work).Any(), "Native workers retained temporary directories.");
        Console.WriteLine("PASS native probe, selected text extraction and six independently decoded stream modes.");
    }
    internal static async Task<(int Video, int Audio)> DecodeAsync(string root, string output, string name, string source)
    {
        string md5 = Path.Combine(output, name + ".md5");
        await NativeEncodingChecks.RunAsync(root, output, name + "-decode", ["--hwdec=no", "--of=framemd5", "--ovc=rawvideo", "--oac=pcm_s16le", "--o=" + md5, source]);
        string[] lines = File.ReadAllLines(md5); var types = new Dictionary<int, string>();
        foreach (string line in lines.Where(l => l.StartsWith("#media_type ", StringComparison.Ordinal)))
        { var values = line[12..].Split(':', 2); types[int.Parse(values[0], CultureInfo.InvariantCulture)] = values[1].Trim(); }
        int video = 0, audio = 0;
        foreach (string line in lines.Where(l => l.Length > 0 && l[0] != '#'))
        { int track = int.Parse(line.Split(',')[0], CultureInfo.InvariantCulture); if (types[track] == "video") video++; else if (types[track] == "audio") audio++; }
        return (video, audio);
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
