using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Native;
using AnimeJaNai.Addons.TestSupport;

internal static class NativeRemoteInputChecks
{
    public static async Task RunAsync(string root, string output, WorkerCommand command, string runtime, string compiler, List<JsonObject> evidence)
    {
        byte[] mediaBytes = await File.ReadAllBytesAsync(await NativeEncodingChecks.FixtureAsync(root, output));
        var requests = new ConcurrentQueue<HttpInput>();
        await using var source = new HttpFixture((request, _) =>
        {
            requests.Enqueue(request);
            if (request.Path == "/forward") return Task.FromResult(new HttpReply(200, mediaBytes, Chunked: true));
            if (request.Path == "/truncated") return Task.FromResult(new HttpReply(200, mediaBytes[..(mediaBytes.Length / 2)], DeclaredLength: mediaBytes.Length));
            if (request.Path == "/slow") return Task.FromResult(new HttpReply(200, mediaBytes, BodyDelay: TimeSpan.FromMinutes(1)));
            int start = int.Parse(request.Headers["Range"][6..^1], CultureInfo.InvariantCulture);
            return Task.FromResult(new HttpReply(206, mediaBytes[start..], $"Content-Range: bytes {start}-{mediaBytes.Length - 1}/{mediaBytes.Length}\r\nETag: \"native-media-1\"\r\n"));
        });
        var uploads = new ConcurrentQueue<HttpInput>();
        await using var receiver = new HttpFixture((request, _) => { uploads.Enqueue(request); return Task.FromResult(new HttpReply(201, [])); }, maximumBody: 8 << 20);
        string area = Path.Combine(output, "managed"); Directory.CreateDirectory(area);
        var package = await DeveloperTools.BuildAsync(Path.Combine(AppContext.BaseDirectory, "remote-inspector"), compiler, Path.Combine(output, "remote-inspector.ajnaddon"));
        var grant = new PermissionGrant(package, package.Manifest.Permissions);
        new AddonRegistry(area).Install(package, grant.Allowed);
        var media = new MediaSelections(area); var network = new NetworkSelections(area);
        string profile = media.ApproveProfile(package, grant, "Synthetic 2x DirectML", 1002, "DirectML", "[global]\nconfig_version=3\nbackend=DirectML\nlogging=yes\n");
        string sourceId = network.Approve(package, grant, "Synthetic source", await NetworkDestination.InspectAsync($"http://127.0.0.1:{source.Port}", default));
        string outputId = network.Approve(package, grant, "Synthetic receiver", await NetworkDestination.InspectAsync($"http://127.0.0.1:{receiver.Port}", default));
        network.SetCredential(package, grant, sourceId, "X-Source-Test", "synthetic-native-input-value");
        network.SetCredential(package, grant, outputId, "X-Receiver-Test", "synthetic-native-receiver-value");
        var sessions = new SessionRegistry(new NativeSessionProvider(root, area, media, command, networkSelections: network), perOwnerLimit: 16);
        await using var service = new AddonService(area, async (p, g, log, token) =>
            await AddonWorker.StartAsync(p, g, runtime, Path.Combine(area, "workers"), area, command, log, sessions,
                cancellationToken: token, networkSelections: network), media, network);
        Task<JsonNode?> Call(string method, JsonObject? parameters = null) => service.InvokeAsync("remote-test", method, parameters ?? new() { ["id"] = package.Manifest.Id }, default);
        Task<JsonNode?> Action(string action) => Call("addons.action", new() { ["id"] = package.Manifest.Id, ["action"] = action });
        Task<JsonNode?> Configure(JsonObject changes) => Call("addons.configure", new() { ["id"] = package.Manifest.Id, ["changes"] = changes });
        async Task<JsonArray> Until(Func<JsonArray, bool> ready, bool allowFailure = false)
        {
            var timeout = Stopwatch.StartNew();
            while (timeout.Elapsed < TimeSpan.FromSeconds(35))
            {
                var state = (JsonArray)(await Action("status"))!;
                Require(allowFailure || state.All(v => v!["state"]?.GetValue<string>() != "failed"), "Remote session failed: " + state);
                if (ready(state)) return state;
                await Task.Delay(50);
            }
            throw new TimeoutException("Remote state deadline: " + await Action("status"));
        }
        async Task Close()
        {
            await Action("close"); await Until(s => s.Count == 0);
            Require(!Directory.EnumerateDirectories(Path.Combine(area, "media-workers")).Any(), "Remote workers retained temporary files.");
        }
        await Call("manager.hello", new() { ["major"] = 1L }); await Call("addons.start");
        var choices = (JsonArray)(await Action("resources"))!["destinations"]!;
        int Index(string id) => choices.Select((v, i) => (v, i)).Single(x => x.v!["id"]!.GetValue<string>() == id).i + 1;
        Require(((JsonArray)(await Action("resources"))!["sources"]!).Count == 0, "Remote playback must not need a local-file grant.");
        Require((await Action("formats"))?["maximumConcurrentSessions"]?.GetValue<int>() == 2 && source.Requests == 0, "Discovery lost capacity or startup accessed media.");
        await Configure(new() { ["sourceIndex"] = (long)Index(sourceId), ["destinationIndex"] = (long)Index(outputId), ["sessionCount"] = 2L, ["useSourceCredential"] = true });
        var opened = await Action("open"); Require(opened?["error"] is null, "Remote opening failed: " + opened);
        var pair = await Until(s => s.Count == 2 && s.All(v => v!["outputWidth"]?.GetValue<long>() == 960 && v["positionSeconds"]?.GetValue<double>() > .4));
        Require(pair.All(v => v!["decoder"]?.GetValue<string>() == "d3d11va" && v["input"]?["seekable"]?.GetValue<bool>() == true), "Remote playback did not retain GPU decoding and range seeking.");
        evidence.Add(new() { ["remoteConcurrent"] = pair.DeepClone(), ["noLocalFileGrant"] = true });
        await Action("pause"); await Until(s => s.All(v => v!["paused"]?.GetValue<bool>() == true));
        await Configure(new() { ["seekSeconds"] = 2L }); await Action("seek");
        var sought = await Until(s => s.All(v => Math.Abs((v!["positionSeconds"]?.GetValue<double>() ?? -99) - 2) < .2 && v["seeking"]?.GetValue<bool>() == false));
        await Action("resume"); await Until(s => s.All(v => v!["positionSeconds"]?.GetValue<double>() > 2.2));
        Require(requests.All(r => r.Headers["X-Source-Test"] == "synthetic-native-input-value" && !r.Headers.ContainsKey("X-Receiver-Test")), "Input credentials changed service scope.");
        evidence.Add(new() { ["pauseSeekResume"] = sought.DeepClone(), ["rangeRequests"] = requests.Count }); await Close();
        Console.WriteLine("PASS remote DirectML sessions, pause, seek, resume and cleanup.");

        await Configure(new() { ["sessionCount"] = 1L, ["sourcePath"] = "/forward", ["useSourceCredential"] = false }); await Action("open");
        var forward = await Until(s => s.Count == 1 && s[0]!["outputWidth"]?.GetValue<long>() == 960 && s[0]!["positionSeconds"]?.GetValue<double>() > .3);
        Require(forward[0]!["input"]?["seekable"]?.GetValue<bool>() == false, "Unknown-length source must be forward-only.");
        Require((await Action("seek"))?["message"]?.GetValue<string>().Contains("seekable") == true, "Example did not explain unavailable seeking.");
        evidence.Add(new() { ["forwardOnly"] = forward.DeepClone() }); await Close();
        Console.WriteLine("PASS forward-only chunked remote media.");

        await Configure(new() { ["sourcePath"] = "/media", ["sessionCount"] = 2L, ["useSourceCredential"] = true, ["useDestinationCredential"] = true }); await Action("send");
        var completed = await Until(s => s.Count == 2 && s.All(v => v!["state"]?.GetValue<string>() == "completed"));
        Require(uploads.Count == 2, "Remote transcode did not deliver both complete streams.");
        Require(completed.All(v => v!["input"]?["errorCode"] is null), "A deliberate short output was treated as input failure.");
        Require(!completed.ToJsonString().Contains("synthetic-native"), "A credential appeared in public status.");
        evidence.Add(new() { ["remoteOutputs"] = completed.DeepClone() }); await Close();
        int index = 0;
        foreach (var upload in uploads)
        {
            Require(upload.Path == "/upload" && upload.Headers["X-Receiver-Test"] == "synthetic-native-receiver-value" && !upload.Headers.ContainsKey("X-Source-Test"), "Receiver credentials changed service scope.");
            string path = Path.Combine(output, "remote-output-" + index + ".mkv"); await File.WriteAllBytesAsync(path, upload.Body);
            await NativeEncodingChecks.VerifyAsync(root, output, "remote-" + index++, path, evidence);
        }
        await Configure(new() { ["sourcePath"] = "/truncated", ["sessionCount"] = 1L }); await Action("open");
        var failed = await Until(s => s.Count == 1 && s[0]!["state"]?.GetValue<string>() == "failed", allowFailure: true);
        Require(failed[0]!["input"]?["errorCode"]?.GetValue<string>() == "input_truncated", "A partial HTTP body was not reported as a source failure: " + failed);
        evidence.Add(new() { ["truncatedSource"] = failed.DeepClone() }); await Close();
        await Configure(new() { ["sourcePath"] = "/slow", ["sessionCount"] = 2L }); await Action("open");
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
            while (requests.Count(r => r.Path == "/slow") < 2) await Task.Delay(20, timeout.Token);
        var closing = Stopwatch.StartNew();
        await Call("network.revoke", new() { ["id"] = package.Manifest.Id, ["expectedHash"] = package.Hash, ["destinationId"] = sourceId }).WaitAsync(TimeSpan.FromSeconds(12));
        Require(!service.HasRunningWorkers && !Directory.EnumerateDirectories(Path.Combine(area, "media-workers")).Any(), "Revocation left blocked input workers alive.");
        Require(((JsonArray)network.List(package, grant)["destinations"]!).Count == 1, "Revocation removed the receiver too.");
        evidence.Add(new() { ["revokedBlockedInput"] = true, ["cleanupMilliseconds"] = closing.Elapsed.TotalMilliseconds });
        Console.WriteLine("PASS real Wasm remote input: concurrent DirectML, pause/seek, forward-only input, NVENC uploads, decoded audio/video, truncation and revocation cleanup.");
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
