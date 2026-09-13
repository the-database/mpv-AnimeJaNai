using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Native;

internal static partial class Checks
{
    private static async Task FrameAndTimerChecks()
    {
        await Test("Frame capability requires the matching private native ABI and library digest", () =>
        {
            string area = Area(); Directory.CreateDirectory(Path.Combine(area, "addon-host"));
            string library = Path.Combine(area, "libmpv-2.dll"), marker = Path.Combine(area, "addon-host", "native-frames.json");
            File.WriteAllBytes(library, [1, 2, 3]); True(!NativeSessionProvider.HasFrameRuntime(area));
            var declaration = new JsonObject { ["privateSampleAbi"] = 1, ["mpvSha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(library))) };
            File.WriteAllText(marker, declaration.ToJsonString()); True(NativeSessionProvider.HasFrameRuntime(area));
            File.WriteAllBytes(library, [3, 2, 1]); True(!NativeSessionProvider.HasFrameRuntime(area));
            File.WriteAllBytes(library, [1, 2, 3]); declaration["privateSampleAbi"] = 2;
            File.WriteAllText(marker, declaration.ToJsonString()); True(!NativeSessionProvider.HasFrameRuntime(area));
        });
        await Test("Binary framing survives buffered JSON, arbitrary bytes and the next response", async () =>
        {
            byte[] pixels = Enumerable.Range(0, FrameRequest.MaxBytes).Select(i => (byte)i).ToArray();
            using var stream = new MemoryStream();
            var writer = new MessageChannel(Stream.Null, stream);
            await writer.WriteBinaryAsync(new() { ["byteLength"] = pixels.Length }, pixels, default);
            await writer.WriteAsync(new() { ["next"] = "Unicode: 日本語" }, default);
            stream.Position = 0;
            var reader = new MessageChannel(stream, Stream.Null);
            True(Contract.Number(await reader.ReadAsync(default), "byteLength") == pixels.Length);
            True((await reader.ReadBinaryAsync(pixels.Length, default)).SequenceEqual(pixels));
            Same(JsonValue.Create("Unicode: 日本語"), (await reader.ReadAsync(default))["next"]);
            await Error("message_too_large", () => reader.ReadBinaryAsync(FrameRequest.MaxBytes + 1, default));
            await Error("worker_exited", () => reader.ReadBinaryAsync(1, default));
        });
        await Test("Timers coalesce missed ticks, replace atomically and cancel queued tickets", async () =>
        {
            var clock = new ManualClock(); var timers = new AddonTimers(clock);
            timers.Set("sample", 20, true); True(timers.Due() is null);
            clock.Now = 120; var ticket = timers.Due()!;
            Same(JsonValue.Create(5L), timers.Take(ticket)!["missedTicks"]);
            True(timers.Due() is null);
            clock.Now = 140; ticket = timers.Due()!;
            timers.Set("sample", 30, false); True(timers.Take(ticket) is null);
            clock.Now = 170; ticket = timers.Due()!;
            True(timers.Take(ticket) is not null && timers.Due() is null);
            timers.Set("cancelled", 16, false); clock.Now = 200; ticket = timers.Due()!;
            timers.Clear("cancelled"); True(timers.Take(ticket) is null);
            for (int i = 0; i < 8; i++) timers.Set("t" + i, 16, true);
            await Error("capacity_exceeded", () => timers.Set("ninth", 20, true));
            timers.Set("t0", 100, true);
            await Error("invalid_request", () => timers.Set("t0", 1, true));
            timers.Close(); True(timers.Due() is null);
            await Error("owner_closed", () => timers.Set("late", 20, true));
        });
        await Test("Frame broker enforces permission, owner, producer capacity and session cleanup", async () =>
        {
            var provider = new FrameProvider(); var registry = new SessionRegistry(provider);
            var package = Package(permissions: ["sessions.manage", "frames.read"]);
            await using var first = new Broker(package, new(package, package.Manifest.Permissions), Area(), sessions: registry);
            await using var second = new Broker(package, new(package, package.Manifest.Permissions), Area(), sessions: registry);
            await using var denied = new Broker(package, new(package, ["sessions.manage"]), Area(), sessions: registry);
            string session = (await first.InvokeAsync("sessions.open", new() { ["sourceId"] = "chosen" }, default))!["sessionId"]!.GetValue<string>();
            JsonObject Request() => new() { ["sessionId"] = session, ["stage"] = "processed", ["format"] = "bgra8", ["width"] = 320L, ["height"] = 180L, ["maxFps"] = 60L };
            await Error("permission_denied", () => denied.InvokeAsync("frames.subscribe", Request(), default));
            await Error("session_not_found", () => second.InvokeAsync("frames.subscribe", Request(), default));
            string id = (await first.InvokeAsync("frames.subscribe", Request(), default))!["subscriptionId"]!.GetValue<string>();
            await Error("capacity_exceeded", () => first.InvokeAsync("frames.subscribe", Request(), default));
            var result = await first.InvokeTransportAsync("frames.read", new() { ["subscriptionId"] = id }, default);
            True(result.Binary.Length == FrameRequest.MaxBytes && result.Binary.Span[10] == 10);
            await Error("subscription_not_found", () => second.InvokeTransportAsync("frames.read", new() { ["subscriptionId"] = id }, default));
            await Error("permission_denied", () => denied.InvokeTransportAsync("frames.read", new() { ["subscriptionId"] = id }, default));
            await first.InvokeAsync("sessions.close", new() { ["sessionId"] = session }, default);
            True(provider.ClosedSubscriptions == 1);
            await Error("subscription_not_found", () => first.InvokeTransportAsync("frames.read", new() { ["subscriptionId"] = id }, default));
        });
        await Test("Frame packet owns its source data and rejects inconsistent bounds", async () =>
        {
            JsonObject metadata = new() { ["width"] = 1L, ["height"] = 1L, ["format"] = "bgra8", ["stage"] = "processed" };
            byte[] source = [1, 2, 3, 255]; var packet = new FramePacket(metadata, source);
            source[0] = 9; metadata["width"] = 100L;
            True(packet.Pixels.Span[0] == 1 && Contract.Number(packet.Metadata, "width") == 1);
            await Error("invalid_frame", () => new FramePacket(metadata, source));
        });
        if (OperatingSystem.IsWindows()) await Test("Private native mapping drops torn, repeated, old-generation and pre-seek frames", async () =>
        {
            using var buffer = new NativeFrameBuffer();
            IntPtr mapping = MapViewOfFile(new IntPtr(buffer.Handle), 6, 0, 0, new UIntPtr(NativeFrameBuffer.Size));
            True(mapping != IntPtr.Zero);
            try
            {
                using var first = buffer.Subscribe(new(1, 1, 30));
                long config = Marshal.ReadInt64(mapping, 16), sequence = 0;
                void Publish(long id, long epoch, long configuration, int bytes = 4, bool torn = false)
                {
                    Marshal.WriteInt64(mapping, 32, ++sequence);
                    Marshal.WriteInt64(mapping, 24, epoch);
                    Marshal.WriteInt64(mapping, 40, id); Marshal.WriteInt64(mapping, 48, configuration); Marshal.WriteInt64(mapping, 56, epoch);
                    Marshal.WriteInt64(mapping, 64, 1250000);
                    foreach (var (offset, value) in new (int, int)[] { (80,1),(84,1),(88,4),(92,bytes),(96,960),(100,720),(112,1),(116,1),(124,1),(128,1),(144,960),(148,720) })
                        Marshal.WriteInt32(mapping, offset, value);
                    Marshal.Copy(new byte[] { 1, 2, 3, 255 }, 0, mapping + 256, 4);
                    if (!torn) Marshal.WriteInt64(mapping, 32, ++sequence);
                }
                Publish(1, 1, config); True(first.ReadLatest()!.Pixels.Span[0] == 1 && first.ReadLatest() is null);
                buffer.InvalidateForSeek(); Publish(2, 1, config); True(first.ReadLatest() is null);
                Publish(3, 2, config); Same(JsonValue.Create(1.25), first.ReadLatest()!.Metadata["ptsSeconds"]);
                first.Dispose(); using var second = buffer.Subscribe(new(1, 1, 30));
                Publish(4, 2, config); True(second.ReadLatest() is null);
                config = Marshal.ReadInt64(mapping, 16);
                Publish(5, 2, config, bytes: int.MaxValue);
                await Error("invalid_frame", () => second.ReadLatest());
                Publish(6, 2, config, torn: true);
                True(second.ReadLatest() is null);
                buffer.Dispose(); await Error("subscription_not_found", () => second.ReadLatest());
            }
            finally { UnmapViewOfFile(mapping); }
        });
    }

    private static async Task FrameRuntimeChecks(string runtime, string compiler, string dotnet)
    {
        await Test("Previously distributed API 1.0 binary runs unchanged on API 1.2 and preserves saved state", async () =>
        {
            var old = AddonPackage.Load(Path.Combine(AppContext.BaseDirectory, "fixtures", "counter-api-1.0.ajnaddon"));
            True(old.Hash == "a3aa62100ff02639763294c4963e7ad335db78052a7b58c9def1617857e21317" && old.Manifest.Api.MinMinor == 0);
            string data = Area(); var grant = new PermissionGrant(old, old.Manifest.Permissions);
            for (int expected = 1; expected <= 2; expected++)
            {
                await using var worker = await AddonWorker.StartAsync(old, grant, runtime, Path.Combine(data, "workers"), data,
                    new(Path.GetFullPath(dotnet), [typeof(AddonWorker).Assembly.Location]));
                var start = await worker.SendEventAsync("start"); Same(JsonValue.Create(expected), start!["starts"]);
                Same(JsonValue.Create(Contract.Minor), start["host"]!["api"]!["minor"]);
                var status = await worker.SendEventAsync("action", new JsonObject { ["id"] = "status" }); Same(JsonValue.Create(expected), status!["starts"]);
                Same(JsonValue.Create("old-client"), await worker.SendEventAsync("echo", JsonValue.Create("old-client")));
            }
        });
        string directory = Area(), source = Path.Combine(directory, "source");
        DeveloperTools.New(source, "org.example.frames");
        File.WriteAllBytes(Path.Combine(source, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(Manifest("org.example.frames", permissions: ["sessions.manage", "frames.read", "storage.read", "storage.write"]) with
            { Api = new() { Major = 1, MinMinor = 2 } }, Contract.Json));
        File.WriteAllText(Path.Combine(source, "addon.js"), """
            let session, subscription, count = 0;
            function onEvent(event, ajn) {
                if (event.name === "start") {
                    session = ajn.sessions.open("selected").sessionId;
                    subscription = ajn.frames.subscribe(session, {width:320,height:180,maxFps:30}).subscriptionId;
                    const frame = ajn.frames.read(subscription);
                    if (frame.pixels.length !== 230400 || frame.pixels[10] !== 10 || frame.pixels[230399] !== 255) throw Error("bad binary frame");
                    // A JSON response after a maximum binary response must stay aligned.
                    if (ajn.info().api.minor < 2) throw Error("bad follow-up response");
                    ajn.timers.set("sample", 25);
                    return frame.pixels.length;
                }
                if (event.name === "timer" && event.data.timerId === "sample") {
                    const frame = ajn.frames.read(subscription);
                    if (!frame || frame.pixels[10] !== 10) throw Error("bad timer frame");
                    ajn.storage.set("ticks", ++count);
                    if (count === 3) ajn.timers.clear("sample");
                }
                if (event.name === "count") return count;
                if (event.name === "close") { ajn.frames.unsubscribe(subscription); ajn.sessions.close(session); }
            }
            """);
        await Test("Actual Wasm reads maximum binary frames and host timer events without corrupting JSON", async () =>
        {
            var package = await DeveloperTools.BuildAsync(source, compiler, Path.Combine(directory, "frames.ajnaddon"));
            var provider = new FrameProvider();
            await using var worker = await AddonWorker.StartAsync(package, new(package, package.Manifest.Permissions), runtime, Path.Combine(directory, "workers"), directory,
                new(Path.GetFullPath(dotnet), [typeof(AddonWorker).Assembly.Location]), sessions: new(provider));
            Same(JsonValue.Create(FrameRequest.MaxBytes), await worker.SendEventAsync("start"));
            var clock = Stopwatch.StartNew();
            while ((await worker.SendEventAsync("count"))!.GetValue<int>() < 3)
            {
                True(clock.Elapsed < TimeSpan.FromSeconds(3)); await Task.Delay(30);
            }
            await Task.Delay(100);
            Same(JsonValue.Create(3), await worker.SendEventAsync("count"));
            await worker.SendEventAsync("close"); True(provider.ClosedSubscriptions == 1);
        });
    }

    private sealed class ManualClock : TimeProvider
    {
        public long Now;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Now;
    }
    private sealed class FrameProvider : IProcessingSessionProvider
    {
        public int ClosedSubscriptions;
        public bool SupportsFrames => true;
        public Task<IProcessingSession> OpenAsync(string sourceId, string? profileId, CancellationToken token) => Task.FromResult<IProcessingSession>(new Session(this));
        private sealed class Session(FrameProvider provider) : IFrameProcessingSession
        {
            public Task<JsonObject> GetStatusAsync(CancellationToken token) => Task.FromResult(new JsonObject { ["state"] = "running" });
            public IFrameSubscription SubscribeFrames(FrameRequest request) => new Subscription(provider, request);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
        private sealed class Subscription(FrameProvider provider, FrameRequest request) : IFrameSubscription
        {
            private bool closed;
            public FramePacket? ReadLatest() => closed ? null : new(new JsonObject {
                ["width"] = (long)request.Width, ["height"] = (long)request.Height, ["format"] = "bgra8", ["stage"] = "processed",
            }, Enumerable.Range(0, request.Width * request.Height * 4).Select(i => (byte)i).ToArray());
            public void Dispose() { if (!closed) { closed = true; provider.ClosedSubscriptions++; } }
        }
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint high, uint low, UIntPtr bytes);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnmapViewOfFile(IntPtr mapping);
}
