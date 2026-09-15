using System.Text;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Native;
using AnimeJaNai.Addons.TestSupport;

internal static class NativeSubtitleChecks
{
    internal static async Task<string> FixtureAsync(string output, string ffmpeg)
    {
        string srt = Path.Combine(output, "selected.srt"), ass = Path.Combine(output, "selected.ass");
        File.WriteAllText(srt, "1\n00:00:00,500 --> 00:00:02,500\nSelected text subtitle\n\n", new UTF8Encoding(false));
        File.WriteAllText(ass, """
            [Script Info]
            ScriptType: v4.00+
            PlayResX: 480
            PlayResY: 360
            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Default,AJNFixtureRectangle,48,&H000000FF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,0,0,7,0,0,0,1
            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:00.50,0:00:02.50,Default,,0,0,0,,{\an7\pos(120,100)}
            """ + "\uE000\nDialogue: 1,0:00:00.50,0:00:02.50,Default,,0,0,0,,{\\an7\\pos(20,30)\\c&H00FF00&\\p1}m 0 0 l 50 0 50 30 0 30\n", new UTF8Encoding(false));
        string fixture = Path.Combine(output, "subtitle-fixture.mkv"), fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");
        await NativeStreamTimingChecks.Ffmpeg(ffmpeg, output, "subtitle-fixture", [
            "-copyts",
            "-f", "lavfi", "-i", "color=black:size=480x360:rate=24:duration=6",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=6",
            "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=6",
            "-i", srt, "-i", ass, "-i", Path.Combine(fixtures, "rectangle.sup"),
            "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:s", "-map", "4:s", "-map", "5:s",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "16", "-c:a", "pcm_s16le", "-c:s", "copy",
            "-attach", Path.Combine(fixtures, "rectangle.ttf"), "-metadata:s:t", "mimetype=application/x-truetype-font",
            "-metadata:s:a:0", "title=440 Hz", "-metadata:s:a:1", "title=880 Hz", fixture]);
        return fixture;
    }

    internal static async Task RunAsync(string root, string output, WorkerCommand command, string ffmpeg, List<JsonObject> evidence)
    {
        string fixture = await FixtureAsync(output, ffmpeg);
        var probe = await NativeProbeProcess.RunAsync(root, fixture, null, Path.Combine(output, "probe-workers"), command, default);
        var subtitles = probe["tracks"]!.AsArray().OfType<JsonObject>().Where(t => t["type"]!.GetValue<string>() == "subtitle").ToArray();
        var audio = probe["tracks"]!.AsArray().OfType<JsonObject>().Where(t => t["type"]!.GetValue<string>() == "audio").ToArray();
        Require(subtitles.Length == 3 && audio.Length == 2 && probe["attachments"]!.AsArray().Count == 1, "Fixture probe lost tracks or its embedded font.");
        string Track(string codec) => subtitles.Single(t => t["codec"]!.GetValue<string>() == codec)["trackId"]!.GetValue<string>();
        string secondAudio = audio[1]["trackId"]!.GetValue<string>();
        foreach (string kind in new[] { "none", "subrip", "ass", "hdmv_pgs_subtitle", "external" })
        {
            var playback = kind switch {
                "none" => new OutputPlayback(1),
                "external" => new(1, SubtitleMode: "burn", ExternalSourceId: "fixture-subtitle"),
                _ => new(1, kind == "ass" ? secondAudio : "default", "burn", Track(kind)),
            };
            var paths = await NativeStreamTimingChecks.Produce(root, output, "burn-" + kind, fixture, command, 1, 2, evidence,
                playback, subtitleSource: kind == "external" ? Path.Combine(output, "selected.srt") : null);
            var pictures = new List<byte[]>();
            foreach (string path in paths) pictures.AddRange(await Pictures(ffmpeg, path));
            Require(pictures.Count == 48, "Subtitle composition changed frame count: " + kind);
            VerifyPictures(kind, pictures);
            if (kind == "ass") await VerifyTone(ffmpeg, paths[0], 880);
            evidence.Add(new() { ["subtitleMode"] = kind, ["frames"] = pictures.Count, ["sourceStartSeconds"] = 1,
                ["cueVisibleAtStart"] = kind != "none", ["cueClearedAtEnd"] = true, ["embeddedFontMeasured"] = kind == "ass" });
            Console.WriteLine("PASS encoded subtitle composition " + kind);
        }

        byte[] text = File.ReadAllBytes(Path.Combine(output, "selected.srt")); int reads = 0;
        await using var upstream = new HttpFixture((request, _) => {
            Require(request.Headers.GetValueOrDefault("X-Subtitle-Test") == "subtitle-fixture", "External subtitle did not use its separate credential.");
            Interlocked.Increment(ref reads);
            return Task.FromResult(new HttpReply(200, text));
        });
        var destination = await NetworkDestination.InspectAsync($"http://127.0.0.1:{upstream.Port}", default);
        var remote = new RemoteInputPlan(destination, "/subtitle", "X-Subtitle-Test", "subtitle-fixture");
        var remotePaths = await NativeStreamTimingChecks.Produce(root, output, "burn-remote-external", fixture, command, 1, 2, evidence,
            new(1, SubtitleMode: "burn", ExternalRemoteSource: new("fixture-remote", "/subtitle", true)), remoteSubtitles: remote);
        var remotePictures = new List<byte[]>();
        foreach (string path in remotePaths) remotePictures.AddRange(await Pictures(ffmpeg, path));
        VerifyPictures("external", remotePictures); Require(reads > 0, "Remote subtitle fixture was not read.");
        evidence.Add(new() { ["remoteExternalSubtitleBurn"] = true, ["separateCredential"] = true });
    }

    private static void VerifyPictures(string kind, List<byte[]> pictures)
    {
        Require(pictures.Count == 48, "Subtitle output must contain two seconds at 24fps.");
        static int Lit(byte[] rgb) => Enumerable.Range(0, rgb.Length / 3).Count(i => rgb[i * 3] > 60 || rgb[i * 3 + 1] > 60 || rgb[i * 3 + 2] > 60);
        Require(Lit(pictures[^1]) == 0, "Subtitle stayed visible after its source-timed cue ended: " + kind);
        if (kind == "none") { Require(pictures.All(p => Lit(p) == 0), "Explicit none rendered a subtitle."); return; }
        Require(Lit(pictures[0]) > 60, "Subtitle missing at a nonzero start inside the cue: " + kind);
        if (kind == "ass")
        {
            var green = Pixels(pictures[0], (r, g, b) => g > 150 && r < 60 && b < 60);
            var red = Pixels(pictures[0], (r, g, b) => r > 150 && g < 60 && b < 60);
            Require(green.Length > 280 && green.All(p => p.X is >= 9 and <= 35 && p.Y is >= 14 and <= 30), "ASS vector styling/position changed.");
            Require(red.Length > 150 && red.All(p => p.X is >= 58 and <= 80 && p.Y is >= 48 and <= 85), "Embedded private-use font glyph was not rendered in the selected style.");
            int box = (red.Max(p => p.X) - red.Min(p => p.X) + 1) * (red.Max(p => p.Y) - red.Min(p => p.Y) + 1);
            Require(red.Length >= box * .85, "Missing-font fallback replaced the original solid rectangular glyph.");
        }
        if (kind == "hdmv_pgs_subtitle")
        {
            var white = Pixels(pictures[0], (r, g, b) => r > 180 && g > 180 && b > 180);
            Require(white.Length > 400 && white.All(p => p.X is >= 49 and <= 90 && p.Y is >= 124 and <= 137), "PGS bitmap position or image changed.");
        }
        // 1.0s start and 2.5s cue end means exactly 36 presented frames contain it.
        Require(pictures.Take(35).All(p => Lit(p) > 60) && pictures.Skip(37).All(p => Lit(p) == 0), "Subtitle timing disagrees with the selected source interval.");
    }
    private static (int X, int Y)[] Pixels(byte[] rgb, Func<byte, byte, byte, bool> predicate) =>
        Enumerable.Range(0, rgb.Length / 3).Where(i => predicate(rgb[3 * i], rgb[3 * i + 1], rgb[3 * i + 2])).Select(i => (i % 240, i / 240)).ToArray();
    private static async Task<byte[][]> Pictures(string ffmpeg, string path)
    {
        string raw = Path.ChangeExtension(path, ".rgb");
        await NativeStreamTimingChecks.Ffmpeg(ffmpeg, Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-pictures",
            ["-i", path, "-an", "-vf", "scale=240:180:flags=area,format=rgb24", "-fps_mode", "passthrough", "-f", "rawvideo", raw]);
        byte[] bytes = await File.ReadAllBytesAsync(raw); const int size = 240 * 180 * 3;
        Require(bytes.Length % size == 0, "Invalid decoded pictures.");
        return Enumerable.Range(0, bytes.Length / size).Select(i => bytes[(i * size)..((i + 1) * size)]).ToArray();
    }
    private static async Task VerifyTone(string ffmpeg, string path, double expected)
    {
        string raw = Path.ChangeExtension(path, ".pcm");
        await NativeStreamTimingChecks.Ffmpeg(ffmpeg, Path.GetDirectoryName(path)!, "selected-audio",
            ["-i", path, "-vn", "-ac", "1", "-ar", "48000", "-f", "s16le", raw]);
        byte[] bytes = await File.ReadAllBytesAsync(raw); int start = 4800, end = Math.Min(bytes.Length / 2, 28800), crossings = 0;
        Require(end > start + 12000, "Selected audio fixture is too short.");
        for (int i = start + 1; i < end; i++) if (BitConverter.ToInt16(bytes, (i - 1) * 2) <= 0 && BitConverter.ToInt16(bytes, i * 2) > 0) crossings++;
        double measured = crossings * 48000.0 / (end - start);
        Require(Math.Abs(measured - expected) < 10, "Wrong audio track encoded: measured " + measured);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
