using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Native;
using AnimeJaNai.Addons.TestSupport;

internal static class NativeLargeReadChecks
{
    internal static async Task RunAsync(string root, string output, WorkerCommand command,
        string runtime, string compiler, string ffmpeg, List<JsonObject> evidence)
    {
        string video = Path.Combine(output, "large-packets.mkv"), fixture = Path.Combine(output, "large-attachment.mkv");
        string subtitles = Path.Combine(output, "source.srt"), attachment = Path.Combine(output, "synthetic.bin");
        File.WriteAllText(subtitles, "1\n00:00:00,100 --> 00:00:01,800\nLarge read subtitle fixture\n\n", new UTF8Encoding(false));
        byte[] payload = new byte[10110966]; new Random(424242).NextBytes(payload); File.WriteAllBytes(attachment, payload);
        await NativeStreamTimingChecks.Ffmpeg(ffmpeg, output, "large-packets", [
            "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=4:duration=2,noise=alls=60:allf=t+u:all_seed=1234",
            "-map", "0:v", "-c:v", "libx264", "-preset", "ultrafast", "-qp", "0", "-g", "1", video]);
        await NativeStreamTimingChecks.Ffmpeg(ffmpeg, output, "large-attachment", ["-i", video, "-i", subtitles, "-map", "0:v", "-map", "1:s", "-c", "copy",
            "-attach", attachment, "-metadata:s:t", "mimetype=application/octet-stream", fixture]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var probeMetrics = new NativeReadMetrics(); JsonObject metadata;
        using (var file = File.OpenRead(fixture)) metadata = NativeProbeWorker.Probe(root, file, new string('0', 64), timeout.Token, probeMetrics);
        Require(probeMetrics.LargestRequest > 32768 && probeMetrics.BytesRead <= 64L * 1024 * 1024, "Probe did not exercise a bounded oversized native request.");
        int index = (int)metadata["tracks"]!.AsArray().Single(t => t!["type"]!.GetValue<string>() == "subtitle")!["streamIndex"]!.GetValue<long>();
        var subtitleMetrics = new NativeReadMetrics();
        using (var file = File.OpenRead(fixture))
        {
            byte[] result = NativeSubtitles.Extract(root, file, index, new(null, 0, 2), timeout.Token, subtitleMetrics);
            Require(Encoding.UTF8.GetString(result).Contains("Large read subtitle fixture"), "Subtitle extraction lost the selected cue.");
        }
        Require(subtitleMetrics.LargestRequest > 32768, "Subtitle extraction did not exercise an oversized native request.");
        string muxOutput = Directory.CreateDirectory(Path.Combine(output, "mux")).FullName;
        var muxMetrics = new NativeReadMetrics();
        using (var file = File.OpenRead(video))
        using (var mux = new NativeMuxer(muxOutput, file, timeout.Token, readMetrics: muxMetrics)) mux.Run(root, "matroska", true, 1);
        Require(muxMetrics.LargestRequest > 32768, "Muxer did not exercise large packet reads.");
        var segments = Directory.GetFiles(muxOutput, "part_*.mkv");
        Require(segments.Length == 2, "Expected two independently readable muxed segments.");
        foreach (string segment in segments)
            await NativeStreamTimingChecks.Ffmpeg(ffmpeg, output, Path.GetFileNameWithoutExtension(segment) + "-decode", ["-v", "error", "-i", segment, "-map", "0:v", "-f", "null", "-"]);
        JsonObject Metrics(NativeReadMetrics value) => new() { ["largestRequest"] = value.LargestRequest, ["oversizedRequests"] = value.LargeRequests, ["bytesRead"] = value.BytesRead };
        evidence.Add(new() { ["probeReads"] = Metrics(probeMetrics), ["subtitleReads"] = Metrics(subtitleMetrics), ["muxReads"] = Metrics(muxMetrics), ["decodedSegments"] = segments.Length });

        // A real Wasm addon calls the production broker and supervised worker.
        // The remote half uses RemoteMediaStream, pinned loopback approval,
        // stable representation validators and a scoped synthetic credential.
        var requests = new ConcurrentQueue<HttpInput>(); long total = new FileInfo(fixture).Length;
        await using var server = new HttpFixture((request, _) =>
        {
            requests.Enqueue(request);
            Require(request.Headers.GetValueOrDefault("X-Ajn-Fixture") == "large-read", "Scoped source credential was missing.");
            long start = long.Parse(request.Headers["Range"][6..^1], CultureInfo.InvariantCulture);
            return Task.FromResult(new HttpReply(206, [], $"Content-Range: bytes {start}-{total - 1}/{total}\r\nETag: \"large-read-v1\"\r\nAccept-Ranges: bytes\r\n", FileBody: fixture, FileOffset: start));
        });
        string source = Path.Combine(output, "addon"), data = Path.Combine(output, "managed");
        DeveloperTools.New(source, "org.example.large-read");
        File.WriteAllText(Path.Combine(source, "manifest.json"), """
            {"schemaVersion":1,"id":"org.example.large-read","name":"Large read regression","version":"1.0.0",
             "api":{"major":1,"minMinor":7},"permissions":["sessions.manage","media.input","network.connect","credentials.use"],
             "requiredCapabilities":{"mediaProbe":{"major":1,"minMinor":0}},"activation":["manual"]}
            """);
        File.WriteAllText(Path.Combine(source, "addon.js"), """
            let probe = null;
            function onEvent(event, ajn) {
                if (event.name === "probe") { probe = ajn.mediaProbe.open(event.data).probeId; return {probeId: probe}; }
                if (event.name === "poll") {
                    const state = ajn.mediaProbe.status(probe);
                    if (state.state === "completed") return ajn.mediaProbe.result(probe);
                    return state;
                }
                if (event.name === "close") { ajn.mediaProbe.close(probe); probe = null; }
            }
            """);
        var package = await DeveloperTools.BuildAsync(source, compiler, Path.Combine(output, "large-read.ajnaddon"));
        var grant = new PermissionGrant(package, package.Manifest.Permissions);
        var media = new MediaSelections(data); var network = new NetworkSelections(data);
        string local = media.ApproveSource(package, grant, fixture);
        string remote = network.Approve(package, grant, "Synthetic range source", await NetworkDestination.InspectAsync($"http://127.0.0.1:{server.Port}", timeout.Token));
        network.SetCredential(package, grant, remote, "X-Ajn-Fixture", "large-read");
        var sessions = new SessionRegistry(new NativeSessionProvider(root, data, media, command, networkSelections: network));
        await using (var worker = await AddonWorker.StartAsync(package, grant, runtime, Path.Combine(output, "workers"), data, command, sessions: sessions, networkSelections: network))
        foreach (bool isRemote in new[] { false, true })
        {
            await worker.SendEventAsync("probe", isRemote ? new JsonObject { ["type"] = "http", ["destinationId"] = remote, ["path"] = "/fixture", ["useCredential"] = true }
                : new JsonObject { ["type"] = "local", ["sourceId"] = local });
            JsonNode? result;
            do
            {
                result = await worker.SendEventAsync("poll", cancellationToken: timeout.Token);
                Require(result?["state"]?.GetValue<string>() != "failed", "Compiled addon probe failed: " + result);
                if (result?["tracks"] is null) await Task.Delay(25, timeout.Token);
            } while (result?["tracks"] is null);
            Require(result["tracks"]!.AsArray().Any(t => t?["type"]?.GetValue<string>() == "subtitle"), "Compiled addon probe lost subtitles.");
            evidence.Add(new() { ["compiledAddonProbe"] = isRemote ? "approved-http-range" : "approved-local", ["tracks"] = result["tracks"]!.DeepClone(), ["httpRequests"] = requests.Count });
            await worker.SendEventAsync("close");
        }
        Require(requests.Count > 0, "Remote test did not read its approved HTTP source.");
        Console.WriteLine("PASS large native probe/subtitle/mux reads and compiled-addon local/HTTP range probes.");
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
