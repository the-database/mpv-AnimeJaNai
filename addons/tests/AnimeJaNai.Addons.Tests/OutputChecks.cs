using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Native;
using AnimeJaNai.Addons.TestSupport;

internal static partial class Checks
{
    private static readonly string[] OutputPermissions = ["sessions.manage", "network.connect", "media.output", "credentials.use"];
    private static OutputRequest Output(string destination) => new(destination, "/media", "POST", false, "h264", "matroska", 4000, LengthSeconds: 2);
    private static JsonObject OutputParameters(string destination) => new() {
        ["sourceId"] = "selected", ["profileId"] = "profile", ["encoding"] = Output(destination).NativeOptions.ToJson(),
        ["destination"] = new JsonObject { ["type"] = "httpUpload", ["destinationId"] = destination, ["path"] = "/media", ["method"] = "POST", ["useCredential"] = false },
    };
    private static async Task<JsonObject> OutputTerminalAsync(IProcessingSession session)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(8))
        {
            var result = await session.GetStatusAsync(default);
            if (result["state"]?.GetValue<string>() is "completed" or "failed") return result;
            await Task.Delay(20);
        }
        throw new TimeoutException("Output did not finish: " + await session.GetStatusAsync(default));
    }
    private static async Task OutputChecks()
    {
        await Test("Producer metadata accepts a complete shared dependency set and retains size limits", () =>
        {
            string area = Area(); Directory.CreateDirectory(Path.Combine(area, "addon-host"));
            var files = new JsonObject();
            foreach (string name in new[] { "mpv.exe", "libmpv-2.dll", "avformat-63.dll", "libstdc++-6.dll" }.Concat(Enumerable.Range(0, 135).Select(i => $"dependency-{i}.dll")))
            {
                File.WriteAllText(Path.Combine(area, name), name);
                files[name] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(area, name))));
            }
            var marker = new JsonObject { ["schemaVersion"] = 1, ["platform"] = "win-x64", ["cRuntime"] = "ucrt",
                ["ffmpegLinkage"] = "shared", ["privateOutputAbi"] = 1, ["files"] = files, ["buildEvidence"] = new string('x', 90_000) };
            string path = Path.Combine(area, "addon-host", NativeCapabilities.FileName);
            File.WriteAllText(path, marker.ToJsonString()); True(NativeSessionProvider.HasOutputRuntime(area));
            files["LIBMPV-2.dll"] = files["libmpv-2.dll"]!.DeepClone();
            File.WriteAllText(path, marker.ToJsonString()); True(!NativeSessionProvider.HasOutputRuntime(area));
            files.Remove("LIBMPV-2.dll"); marker["buildEvidence"] = new string('x', 1024 * 1024);
            File.WriteAllText(path, marker.ToJsonString()); True(!NativeSessionProvider.HasOutputRuntime(area));
        });
        await Test("Producer metadata supports static UCRT and rejects changed, incomplete or incompatible native sets", () =>
        {
            string area = Area(); Directory.CreateDirectory(Path.Combine(area, "addon-host"));
            File.WriteAllBytes(Path.Combine(area, "mpv.exe"), [1, 2]);
            File.WriteAllBytes(Path.Combine(area, "libmpv-2.dll"), [3, 4]);
            var files = new JsonObject();
            foreach (string name in new[] { "mpv.exe", "libmpv-2.dll" })
                files[name] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(area, name))));
            var marker = new JsonObject { ["schemaVersion"] = 1, ["platform"] = "win-x64", ["cRuntime"] = "ucrt",
                ["ffmpegLinkage"] = "static", ["privateSampleAbi"] = 1, ["privateOutputAbi"] = 1, ["privatePlayerSampleAbi"] = 1, ["files"] = files };
            string path = Path.Combine(area, "addon-host", NativeCapabilities.FileName);
            void Save() => File.WriteAllText(path, marker.ToJsonString());
            Save(); True(NativeSessionProvider.HasOutputRuntime(area) && NativeSessionProvider.HasFrameRuntime(area));
            True(NativePlayerObservations.Available(area));
            marker["schemaVersion"] = 2; Save(); True(!NativeSessionProvider.HasOutputRuntime(area));
            marker["schemaVersion"] = 1; marker["ffmpegLinkage"] = "shared"; Save(); True(!NativeSessionProvider.HasOutputRuntime(area));
            files["avformat-63.dll"] = new string('0', 64); Save(); True(!NativeSessionProvider.HasOutputRuntime(area));
            files.Remove("avformat-63.dll"); marker["ffmpegLinkage"] = "static";
            marker["privateOutputAbi"] = 2; Save(); True(!NativeSessionProvider.HasOutputRuntime(area));
            marker["privateOutputAbi"] = 1; marker["cRuntime"] = "msvcrt"; Save(); True(!NativeSessionProvider.HasOutputRuntime(area));
            marker["cRuntime"] = "ucrt"; files["../outside.dll"] = new string('0', 64); Save(); True(!NativeSessionProvider.HasOutputRuntime(area));
            files.Remove("../outside.dll"); Save();
            File.WriteAllBytes(Path.Combine(area, "mpv.exe"), [9]); True(!NativeSessionProvider.HasOutputRuntime(area));
            File.WriteAllText(path, "{"); True(!NativeSessionProvider.HasOutputRuntime(area));
        });
        await Test("Native output capability requires matching player, UCRT muxer and private ABI", () =>
        {
            string area = Area(); Directory.CreateDirectory(Path.Combine(area, "addon-host"));
            string player = Path.Combine(area, "libmpv-2.dll"), muxer = Path.Combine(area, "avformat-63.dll"), path = Path.Combine(area, "addon-host", "native-output.json");
            File.WriteAllBytes(player, [1, 2, 3]); File.WriteAllBytes(muxer, [4, 5, 6]);
            True(!NativeSessionProvider.HasOutputRuntime(area));
            var marker = new JsonObject { ["privateOutputAbi"] = 1, ["cRuntime"] = "ucrt",
                ["mpvSha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(player))),
                ["avformatSha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(muxer))) };
            File.WriteAllText(path, marker.ToJsonString()); True(NativeSessionProvider.HasOutputRuntime(area));
            marker["cRuntime"] = "other"; File.WriteAllText(path, marker.ToJsonString()); True(!NativeSessionProvider.HasOutputRuntime(area));
            marker["cRuntime"] = "ucrt"; File.WriteAllText(path, marker.ToJsonString()); File.WriteAllBytes(muxer, [7]); True(!NativeSessionProvider.HasOutputRuntime(area));
        });
        await Test("Output API keeps capability discovery separate from all three permissions", async () =>
        {
            string area = Area(); var package = Package(permissions: OutputPermissions);
            var provider = new OutputProvider(new(area), []); var registry = new SessionRegistry(provider);
            await using var broker = new Broker(package, new(package, OutputPermissions), area, sessions: registry);
            True(broker.Info()["capabilities"]?["outputs"] is not null);
            foreach (string permission in new[] { "media.output", "sessions.manage", "network.connect" })
            {
                await using var denied = new Broker(package, new(package, OutputPermissions.Where(p => p != permission).ToArray()), area, sessions: registry);
                await Error("permission_denied", () => denied.InvokeAsync("outputs.open", OutputParameters("unapproved"), default));
            }
            await Error("destination_not_granted", () => broker.InvokeAsync("outputs.open", OutputParameters("unapproved"), default));
            True(provider.Opened == 0);
            await using var noNative = new Broker(package, new(package, OutputPermissions), Area());
            True(noNative.Info()["capabilities"]?["outputs"] is null);
            await Error("feature_unavailable", () => noNative.InvokeAsync("outputs.formats", new(), default));
        });
        await Test("Output review binds package, relative authority, protocol and optional scoped credential", async () =>
        {
            string area = Area(); var selections = new NetworkSelections(area); var package = Package(permissions: OutputPermissions); var grant = new PermissionGrant(package, OutputPermissions);
            string id = selections.Approve(package, grant, "Local service", await NetworkDestination.InspectAsync("http://127.0.0.1:18080", default));
            var request = Output(id);
            _ = OutputUploadPlan.Prepare(package, grant, selections, request);
            await Error("invalid_request", () => OutputUploadPlan.Prepare(package, grant, selections, request with { Path = "//another.test/" }));
            await Error("invalid_request", () => OutputUploadPlan.Prepare(package, grant, selections, request with { Path = "https://another.test/" }));
            await Error("credential_not_granted", () => OutputUploadPlan.Prepare(package, grant, selections, request with { UseCredential = true }));
            var other = Package("org.example.other", permissions: OutputPermissions);
            await Error("destination_not_granted", () => OutputUploadPlan.Prepare(other, new(other, OutputPermissions), selections, request));
            string udp = selections.Approve(package, grant, "Device", await NetworkDestination.InspectAsync("udp://127.0.0.1:18080", default));
            await Error("invalid_output", () => OutputUploadPlan.Prepare(package, grant, selections, request with { DestinationId = udp }));
            await Error("invalid_encoding", () => (request with { VideoKbps = 50001 }).Validate());
        });
        await Test("Encoded upload sends exact chunked bytes to pinned HTTP authority without inherited state", async () =>
        {
            byte[] bytes = Enumerable.Range(0, 2 * 1024 * 1024).Select(i => (byte)(i * 17)).ToArray();
            await using var server = new HttpFixture((_, _) => Task.FromResult(new HttpReply(201, [])), maximumBody: bytes.Length);
            string area = Area(); var selections = new NetworkSelections(area); var package = Package(permissions: OutputPermissions); var grant = new PermissionGrant(package, OutputPermissions);
            string id = selections.Approve(package, grant, "Pinned service", new($"http://output.example.invalid:{server.Port}", "http", "output.example.invalid", server.Port, ["127.0.0.1"]));
            var native = new EncodedFixture(bytes);
            await using var session = new OutputUploadSession(native, OutputUploadPlan.Prepare(package, grant, selections, Output(id)));
            var result = await OutputTerminalAsync(session);
            True(result["state"]!.GetValue<string>() == "completed", result.ToJsonString());
            True(result["output"]!["bytesSent"]!.GetValue<long>() == bytes.Length && native.Closed);
            True(server.Last!.Body.SequenceEqual(bytes)); True(server.Last.Headers["Host"].StartsWith("output.example.invalid:"));
            True(server.Last.Headers["Content-Type"] == "video/x-matroska" && server.Last.Headers["Transfer-Encoding"] == "chunked");
            True(!server.Last.Headers.ContainsKey("Cookie") && !server.Last.Headers.ContainsKey("Authorization"));
        });
        await Test("Encoded upload rejects redirects and early success, and cleans up a failed producer", async () =>
        {
            await using var unapproved = new HttpFixture((_, _) => Task.FromResult(new HttpReply(200, [])));
            foreach (var (status, delay) in new[] { (302, 0), (200, 0), (413, 0), (302, 500), (200, 500), (413, 500) })
            {
                await using var server = new HttpFixture(async (_, token) => { await Task.Delay(delay, token); return new HttpReply(status, [], $"Location: http://127.0.0.1:{unapproved.Port}/\r\n"); }, headersOnly: true);
                string area = Area(); var selections = new NetworkSelections(area); var package = Package(permissions: OutputPermissions); var grant = new PermissionGrant(package, OutputPermissions);
                string id = selections.Approve(package, grant, "Early response", await NetworkDestination.InspectAsync($"http://127.0.0.1:{server.Port}", default));
                var native = new EncodedFixture(new DelayedOutput());
                await using var session = new OutputUploadSession(native, OutputUploadPlan.Prepare(package, grant, selections, Output(id)));
                var result = await OutputTerminalAsync(session);
                True(result["state"]!.GetValue<string>() == "failed" && native.Closed, result.ToJsonString());
                True(result["output"]!["httpStatus"]!.GetValue<int>() == status);
            }
            True(unapproved.Requests == 0);
        });
        await Test("Upload observer handles fragmented interim responses and bounded malformed metadata", async () =>
        {
            foreach (var item in new[] {
                (Text: "HTTP/1.1 103 Early Hints\r\nLink: /test\r\n\r\nHTTP/1.1 100 Continue\r\n\r\nHTTP/1.1 413 Too Large\r\nContent-Length: 0\r\n\r\n", Status: (int?)413),
                (Text: "HTTP/1.1 200 OK\r\nX-Data: " + new string('x', 17000) + "\r\n\r\n", Status: (int?)null),
                (Text: "invalid\r\n\r\n", Status: (int?)null),
            })
            {
                int calls = 0; int? seen = -1;
                using var stream = new UploadResponseStream(new MemoryStream(Encoding.ASCII.GetBytes(item.Text)), status => { calls++; seen = status; });
                byte[] one = new byte[1]; True(await stream.ReadAsync(Memory<byte>.Empty) == 0 && calls == 0);
                while (await stream.ReadAsync(one) != 0) { }
                True(calls == 1 && seen == item.Status);
            }
        });
        await Test("Encoded upload cannot turn native failure or failed cleanup into successful completion", async () =>
        {
            await using var server = new HttpFixture((_, _) => Task.FromResult(new HttpReply(200, [])));
            string area = Area(); var selections = new NetworkSelections(area); var package = Package(permissions: OutputPermissions); var grant = new PermissionGrant(package, OutputPermissions);
            string id = selections.Approve(package, grant, "Receiver", await NetworkDestination.InspectAsync($"http://127.0.0.1:{server.Port}", default));
            var failedNative = new EncodedFixture([1, 2, 3]) { NativeState = "failed" };
            await using (var failed = new OutputUploadSession(failedNative, OutputUploadPlan.Prepare(package, grant, selections, Output(id))))
            {
                var status = await OutputTerminalAsync(failed);
                True(status["state"]!.GetValue<string>() == "failed" && status["error"]!["code"]!.GetValue<string>() == "native_output_failed" && failedNative.Closed);
            }
            var retained = new EncodedFixture([4, 5, 6]) { FailCleanup = true };
            await using var retry = new OutputUploadSession(retained, OutputUploadPlan.Prepare(package, grant, selections, Output(id)));
            True((await OutputTerminalAsync(retry))["error"]!["code"]!.GetValue<string>() == "cleanup_pending" && !retained.Closed);
            await Error("cleanup_pending", () => retry.DisposeAsync().AsTask());
            retained.FailCleanup = false; await retry.DisposeAsync(); True(retained.Closed);
        });
        await Test("Encoded uploads bound throughput and cancel while waiting for bandwidth", async () =>
        {
            byte[] bytes = new byte[16 * 1024 * 1024 + 1];
            await using var server = new HttpFixture((_, _) => Task.FromResult(new HttpReply(200, [])), maximumBody: bytes.Length);
            string area = Area(); var selections = new NetworkSelections(area); var package = Package(permissions: OutputPermissions); var grant = new PermissionGrant(package, OutputPermissions);
            string id = selections.Approve(package, grant, "Receiver", await NetworkDestination.InspectAsync($"http://127.0.0.1:{server.Port}", default));
            var clock = Stopwatch.StartNew();
            await using (var session = new OutputUploadSession(new EncodedFixture(bytes), OutputUploadPlan.Prepare(package, grant, selections, Output(id))))
            {
                var status = await OutputTerminalAsync(session);
                True(status["state"]!.GetValue<string>() == "completed", status.ToJsonString());
                True(clock.Elapsed >= TimeSpan.FromSeconds(1.8), "Stream bypassed its 8 MiB/s windows.");
            }
            var native = new EncodedFixture(bytes);
            await using var cancelled = new OutputUploadSession(native, OutputUploadPlan.Prepare(package, grant, selections, Output(id)));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while ((await cancelled.GetStatusAsync(default))["output"]!["bytesSent"]!.GetValue<long>() < 8 * 1024 * 1024) await Task.Delay(10, timeout.Token);
            clock.Restart(); await cancelled.DisposeAsync(); True(clock.Elapsed < TimeSpan.FromSeconds(3) && native.Closed);
            True((await cancelled.GetStatusAsync(default))["output"]!["bytesSent"]!.GetValue<long>() < bytes.Length);
        });
        await Test("Output ownership shares session admission and cleanup without blocking other owners", async () =>
        {
            await using var server = new HttpFixture(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new(200, []); });
            string area = Area(); var selections = new NetworkSelections(area); var package = Package(permissions: OutputPermissions); var grant = new PermissionGrant(package, OutputPermissions);
            string id = selections.Approve(package, grant, "Silent service", await NetworkDestination.InspectAsync($"http://127.0.0.1:{server.Port}", default));
            var provider = new OutputProvider(selections, new byte[1024]); var registry = new SessionRegistry(provider, totalLimit: 2, perOwnerLimit: 2);
            await using var first = new Broker(package, grant, area, sessions: registry);
            await using var second = new Broker(package, grant, area, sessions: registry);
            string one = (await first.InvokeAsync("outputs.open", OutputParameters(id), default))!["sessionId"]!.GetValue<string>();
            string two = (await first.InvokeAsync("outputs.open", OutputParameters(id), default))!["sessionId"]!.GetValue<string>();
            await Error("session_not_found", () => second.InvokeAsync("sessions.status", new() { ["sessionId"] = one }, default));
            await Error("capacity_exceeded", () => second.InvokeAsync("outputs.open", OutputParameters(id), default));
            await Error("operation_unavailable", () => first.InvokeAsync("sessions.seek", new() { ["sessionId"] = two, ["seconds"] = 1d }, default));
            var clock = Stopwatch.StartNew(); await first.DisposeAsync(); True(clock.Elapsed < TimeSpan.FromSeconds(4));
            _ = await second.InvokeAsync("outputs.open", OutputParameters(id), default);
            True(provider.Opened == 3);
        });
        if (OperatingSystem.IsWindows()) await Test("Encoded upload uses a protected header only with explicit credential permission", async () =>
        {
            await using var server = new HttpFixture((_, _) => Task.FromResult(new HttpReply(204, [])));
            string area = Area(); var selections = new NetworkSelections(area); var package = Package(permissions: OutputPermissions); var grant = new PermissionGrant(package, OutputPermissions);
            string id = selections.Approve(package, grant, "Credential service", await NetworkDestination.InspectAsync($"http://127.0.0.1:{server.Port}", default));
            selections.SetCredential(package, grant, id, "X-Output-Fixture", "synthetic-output-value");
            await Error("permission_denied", () => OutputUploadPlan.Prepare(package, new(package, OutputPermissions.Where(p => p != "credentials.use").ToArray()), selections, Output(id) with { UseCredential = true }));
            await using var session = new OutputUploadSession(new EncodedFixture([1, 2, 3]), OutputUploadPlan.Prepare(package, grant, selections, Output(id) with { UseCredential = true }));
            var result = await OutputTerminalAsync(session); True(result["state"]!.GetValue<string>() == "completed");
            True(server.Last!.Headers["X-Output-Fixture"] == "synthetic-output-value");
            True(!result.ToJsonString().Contains("synthetic-output-value"));
        });
    }

    private static async Task OutputRuntimeChecks(string runtime, string compiler, string dotnet)
    {
        await Test("Shipped Wasm output inspector uses reviewed resources, multiple owned outputs and saved settings", async () =>
        {
            byte[] bytes = Enumerable.Range(0, 512 * 1024).Select(i => (byte)(i * 37)).ToArray();
            await using var server = new HttpFixture((_, _) => Task.FromResult(new HttpReply(201, [])), maximumBody: bytes.Length);
            string area = Area();
            var package = await DeveloperTools.BuildAsync(Path.Combine(AppContext.BaseDirectory, "output-inspector"), compiler, Path.Combine(area, "inspector.ajnaddon"));
            var grant = new PermissionGrant(package, package.Manifest.Permissions); var selections = new NetworkSelections(area);
            string id = selections.Approve(package, grant, "Synthetic receiver", LoopbackDestination(server.Port));
            selections.SetCredential(package, grant, id, "X-Output-Inspector", "synthetic-inspector-output-value");
            var provider = new OutputProvider(selections, bytes); var registry = new SessionRegistry(provider, totalLimit: 2, perOwnerLimit: 2);
            var settings = new AddonSettings(area, package.Manifest); settings.Update(new() { ["sessionCount"] = 2L });
            await using var worker = await AddonWorker.StartAsync(package, grant, runtime, Path.Combine(area, "workers"), area,
                new(Path.GetFullPath(dotnet), [typeof(AddonWorker).Assembly.Location]), sessions: registry, networkSelections: selections);
            await worker.SendEventAsync("start"); True(provider.Opened == 0 && server.Requests == 0);
            Task<JsonNode?> Action(string action) => worker.SendEventAsync("action", new JsonObject { ["id"] = action });
            True(((JsonArray)(await Action("resources"))!["sources"]!).Count == 1);
            True(((JsonArray)(await Action("formats"))!["videoCodecs"]!).Any(v => v!.GetValue<string>() == "h264"));
            var opened = await Action("open"); True(((JsonArray)opened!["sessions"]!).Count == 2, opened.ToJsonString());
            async Task Complete(int count)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                while (true)
                {
                    var statuses = (JsonArray)(await Action("status"))!;
                    True(statuses.Count == count && statuses.All(s => s!["state"]!.GetValue<string>() != "failed"), statuses.ToJsonString());
                    if (statuses.All(s => s!["state"]!.GetValue<string>() == "completed"))
                    {
                        True(statuses.All(s => s!["output"]!["bytesSent"]!.GetValue<long>() == bytes.Length));
                        True(!statuses.ToJsonString().Contains("synthetic-inspector-output-value")); return;
                    }
                    await Task.Delay(30, timeout.Token);
                }
            }
            await Complete(2);
            True(server.Requests == 2 && server.Last!.Body.SequenceEqual(bytes) && !server.Last.Headers.ContainsKey("X-Output-Inspector"));
            await Action("close");
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                while (((JsonArray)(await Action("status"))!).Count != 0) await Task.Delay(20, timeout.Token);
            settings.Update(new() { ["sessionCount"] = 1L, ["useCredential"] = true, ["method"] = "PUT", ["container"] = "mpegts", ["path"] = "/changed" });
            await worker.SendEventAsync("settings.changed"); await Action("open"); await Complete(1);
            True(provider.Opened == 3 && server.Last!.Path == "/changed" && server.Last.Method == "PUT");
            True(server.Last!.Headers["Content-Type"] == "video/mp2t" && server.Last.Headers["X-Output-Inspector"] == "synthetic-inspector-output-value");
            await worker.SendEventAsync("stop");
        });
    }

    private sealed class EncodedFixture(Stream stream) : IEncodedProcessingSession
    {
        public EncodedFixture(byte[] bytes) : this(new MemoryStream(bytes, writable: false)) { }
        public bool Closed;
        public bool FailCleanup;
        public string NativeState = "completed";
        public Stream EncodedOutput => stream;
        public Task<JsonObject> GetStatusAsync(CancellationToken token) => Task.FromResult(new JsonObject { ["state"] = Closed ? "closed" : NativeState, ["outputWidth"] = 960 });
        public Task PauseAsync(bool paused, CancellationToken token) => Task.CompletedTask;
        public Task SeekAsync(double seconds, CancellationToken token) => Task.CompletedTask;
        public ValueTask DisposeAsync()
        {
            if (FailCleanup) throw new AddonException("cleanup_pending", "Synthetic producer cleanup failure.");
            Closed = true; stream.Dispose(); return ValueTask.CompletedTask;
        }
    }
    private sealed class DelayedOutput : Stream
    {
        private readonly CancellationTokenSource stop = new();
        private bool started;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (!started)
            {
                started = true;
                int length = Math.Min(16384, buffer.Length);
                buffer.Span[..length].Fill(42);
                return length;
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token);
            await Task.Delay(Timeout.Infinite, linked.Token);
            return 0;
        }
        protected override void Dispose(bool disposing) { stop.Cancel(); base.Dispose(disposing); }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException(); public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class OutputProvider(NetworkSelections selections, byte[] bytes) : IProcessingSessionProvider
    {
        public int Opened;
        public bool SupportsOutputs => true;
        public int ApiMinor => 1;
        public JsonObject OutputFormats() => new() { ["videoCodecs"] = new JsonArray("h264") };
        public Task<IProcessingSession> OpenAsync(string sourceId, string? profileId, CancellationToken token) => throw new NotSupportedException();
        public IProcessingSessionProvider ForAddon(AddonPackage package, PermissionGrant grant) => new Bound(this, package, grant);
        private sealed class Bound(OutputProvider parent, AddonPackage package, PermissionGrant grant) : IProcessingSessionProvider, IProcessingSelectionProvider
        {
            public bool SupportsOutputs => true;
            public int ApiMinor => 1;
            public JsonObject OutputFormats() => parent.OutputFormats();
            public JsonObject ListSelections() => new() {
                ["sources"] = new JsonArray(new JsonObject { ["id"] = "selected", ["name"] = "Synthetic video" }),
                ["profiles"] = new JsonArray(new JsonObject { ["id"] = "profile", ["name"] = "Synthetic profile", ["slot"] = 1002, ["backend"] = "DirectML" }),
                ["maximumConcurrentSessions"] = 2,
            };
            public Task<IProcessingSession> OpenAsync(string sourceId, string? profileId, CancellationToken token) => throw new NotSupportedException();
            public Task<IProcessingSession> OpenOutputAsync(string sourceId, string? profileId, OutputRequest output, CancellationToken token)
            {
                True(sourceId == "selected" && profileId == "profile");
                var plan = OutputUploadPlan.Prepare(package, grant, parent.Selections, output); parent.Opened++;
                return Task.FromResult<IProcessingSession>(new OutputUploadSession(new EncodedFixture(parent.Bytes), plan));
            }
        }
        private NetworkSelections Selections => selections;
        private byte[] Bytes => bytes;
    }
}
