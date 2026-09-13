using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Native;
using AnimeJaNai.Addons.TestSupport;

internal static class NativeOutputChecks
{
    public static async Task RunAsync(string root, string output, WorkerCommand command, string runtime, string compiler, List<JsonObject> evidence)
    {
        Require(NativeSessionProvider.HasOutputRuntime(root), "A hash-bound native output runtime is required.");
        string source = await NativeEncodingChecks.FixtureAsync(root, output);
        string area = Path.Combine(output, "managed"); Directory.CreateDirectory(area);
        var received = new ConcurrentQueue<(string Path, HttpInput Input)>(); int sequence = 0;
        await using var server = new HttpFixture(async (input, token) =>
        {
            string path = Path.Combine(output, "received-" + Interlocked.Increment(ref sequence) + ".media");
            await File.WriteAllBytesAsync(path, input.Body, token); received.Enqueue((path, input)); return new(201, []);
        }, maximumBody: 8 * 1024 * 1024);
        await using var silent = new HttpFixture(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new(200, []); }, headersOnly: true);
        var package = await DeveloperTools.BuildAsync(Path.Combine(AppContext.BaseDirectory, "output-inspector"), compiler, Path.Combine(output, "output-inspector.ajnaddon"));
        var grant = new PermissionGrant(package, package.Manifest.Permissions);
        new AddonRegistry(area).Install(package, package.Manifest.Permissions);
        var media = new MediaSelections(area); var network = new NetworkSelections(area);
        string sourceId = media.ApproveSource(package, grant, source);
        media.ApproveProfile(package, grant, "Synthetic 2x DirectML", 1002, "DirectML", "[global]\nconfig_version=3\nbackend=DirectML\nlogging=yes\n");
        string destination = network.Approve(package, grant, "Loopback test receiver", await NetworkDestination.InspectAsync($"http://127.0.0.1:{server.Port}", default));
        network.SetCredential(package, grant, destination, "X-Output-Test", "synthetic-native-output-value");
        string slow = network.Approve(package, grant, "Unread test receiver", await NetworkDestination.InspectAsync($"http://127.0.0.1:{silent.Port}", default));
        var sessions = new SessionRegistry(new NativeSessionProvider(root, area, media, command, networkSelections: network), perOwnerLimit: 16);
        await using var service = new AddonService(area, async (p, g, log, token) =>
            await AddonWorker.StartAsync(p, g, runtime, Path.Combine(area, "workers"), area, command, log, sessions,
                cancellationToken: token, networkSelections: network), media, network);
        Task<JsonNode?> Call(string method, JsonObject? parameters = null) => service.InvokeAsync("output-test", method, parameters ?? new() { ["id"] = package.Manifest.Id }, default);
        Task<JsonNode?> Action(string id) => Call("addons.action", new() { ["id"] = package.Manifest.Id, ["action"] = id });
        Task<JsonNode?> Configure(JsonObject changes) => Call("addons.configure", new() { ["id"] = package.Manifest.Id, ["changes"] = changes });
        async Task<JsonArray> Until(Func<JsonArray, bool> ready, bool allowFailure = false)
        {
            var timeout = Stopwatch.StartNew();
            while (timeout.Elapsed < TimeSpan.FromSeconds(30))
            {
                var state = (JsonArray)(await Action("status"))!;
                Require(allowFailure || state.All(v => v!["state"]?.GetValue<string>() != "failed"), "Output failed: " + state);
                if (ready(state)) return state;
                await Task.Delay(40);
            }
            throw new TimeoutException("Native upload state deadline: " + await Action("status"));
        }
        async Task Close()
        {
            await Action("close"); await Until(s => s.Count == 0);
            Require(!Directory.EnumerateDirectories(Path.Combine(area, "media-workers")).Any(), "Output workers retained files after closing.");
        }
        await Call("manager.hello", new() { ["major"] = 1L });
        await Call("addons.start");
        var formats = await Action("formats"); Require(formats?["maximumConcurrentSessions"]?.GetValue<int>() == 2, "Output discovery lost host capacity.");
        Require(server.Requests == 0 && received.IsEmpty, "Starting the inspector sent media.");
        foreach (var (codec, container, audio, method) in new[] { ("h264", "matroska", "aac", "POST"), ("hevc", "mpegts", "aac", "PUT"), ("av1", "fragmentedMp4", "opus", "POST") })
        {
            await Configure(new() { ["videoCodec"] = codec, ["container"] = container, ["audioCodec"] = audio, ["method"] = method,
                ["useCredential"] = true, ["path"] = "/" + codec, ["sessionCount"] = 1L, ["lengthSeconds"] = 2L });
            var opened = await Action("open"); Require(opened?["error"] is null, "Open failed: " + opened);
            var completed = await Until(s => s.Count == 1 && s[0]!["state"]?.GetValue<string>() == "completed");
            Require(completed[0]!["pixelFormat"]?.GetValue<string>() == "d3d11", "Output did not preserve the GPU path.");
            Require(received.TryDequeue(out var upload), "Receiver has no complete upload.");
            Require(upload.Input.Method == method && upload.Input.Path == "/" + codec && upload.Input.Headers["X-Output-Test"] == "synthetic-native-output-value", "Output destination or credential changed.");
            Require(completed[0]!["output"]!["bytesSent"]!.GetValue<long>() == upload.Input.Body.Length, "Delivered length does not match status.");
            Require(!completed.ToJsonString().Contains("synthetic-native-output-value"), "Credential was exposed in status.");
            evidence.Add(new() { ["httpNativeOutput"] = codec, ["bytes"] = upload.Input.Body.Length, ["status"] = completed.DeepClone() });
            await Close();
            await NativeEncodingChecks.VerifyAsync(root, output, "http-" + codec, upload.Path, evidence);
        }
        await Configure(new() { ["videoCodec"] = "h264", ["container"] = "matroska", ["audioCodec"] = "aac", ["sessionCount"] = 2L });
        await Action("open");
        var pair = await Until(s => s.Count == 2 && s.All(v => v!["state"]?.GetValue<string>() == "completed"));
        Require(received.Count == 2, "Independent outputs did not deliver two streams.");
        evidence.Add(new() { ["concurrentHttpOutputs"] = pair.DeepClone() }); await Close();
        while (received.TryDequeue(out _)) { }

        // A slow destination can fill its socket and pipe, but revoking access
        // must stop both its addon and all owned producers before removing it.
        var choices = (JsonArray)(await Action("resources"))!["destinations"]!;
        int slowIndex = choices.Select((v, i) => (v, i)).Single(x => x.v!["id"]!.GetValue<string>() == slow).i + 1;
        await Configure(new() { ["destinationIndex"] = (long)slowIndex, ["sessionCount"] = 2L, ["lengthSeconds"] = 0L, ["useCredential"] = false });
        await Action("open");
        var pending = await Until(s => s.Count == 2 && s.All(v => v!["output"]!["bytesSent"]?.GetValue<long>() > 0));
        var close = Stopwatch.StartNew();
        await Call("network.revoke", new() { ["id"] = package.Manifest.Id, ["expectedHash"] = package.Hash, ["destinationId"] = slow }).WaitAsync(TimeSpan.FromSeconds(12));
        Require(!service.HasRunningWorkers && !Directory.EnumerateDirectories(Path.Combine(area, "media-workers")).Any(), "Revocation left output workers alive.");
        Require(((JsonArray)network.List(package, grant)["destinations"]!).Count == 1, "Revocation removed the wrong destination.");
        evidence.Add(new() { ["revokedActiveDestination"] = true, ["closeMilliseconds"] = close.Elapsed.TotalMilliseconds, ["prior"] = pending.DeepClone() });

        await Configure(new() { ["destinationIndex"] = 1L, ["sessionCount"] = 1L, ["lengthSeconds"] = 2L });
        await Call("addons.start"); await Action("open");
        await Until(s => s.Count == 1 && s[0]!["state"]?.GetValue<string>() is "opening" or "running" or "finishing" or "completed");
        await Call("media.revoke", new() { ["id"] = package.Manifest.Id, ["expectedHash"] = package.Hash, ["kind"] = "source", ["selectionId"] = sourceId });
        Require(!service.HasRunningWorkers && !Directory.EnumerateDirectories(Path.Combine(area, "media-workers")).Any(), "Source revocation left output workers alive.");
        Require(((JsonArray)media.List(package, grant)["sources"]!).Count == 0, "Source was not revoked.");
        evidence.Add(new() { ["sourceRevocation"] = true, ["cleanup"] = "all owned output workers released" });
        Console.WriteLine("PASS real Wasm native HTTP output: three decoded codecs, audio/color/timestamps, concurrent streams, source/destination revocation and cleanup.");
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
