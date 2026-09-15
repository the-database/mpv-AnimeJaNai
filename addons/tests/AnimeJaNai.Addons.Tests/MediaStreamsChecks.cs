using System.Text.Json.Nodes;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Native;

internal static partial class Checks
{
    private static async Task MediaStreamsChecks()
    {
        await Test("Stream permissions are independent and handles remain private across workers and failure cleanup", async () =>
        {
            string[] permissions = ["sessions.manage", "media.input", "media.output"];
            var package = Package(permissions: permissions);
            var options = new StreamRequest(new("h264", "matroska", 1000), new(), true, 1);
            foreach (string denied in permissions)
            {
                var missing = new PermissionGrant(package, permissions.Where(p => p != denied));
                await using var restricted = new Broker(package, missing, Area(), sessions: new SessionRegistry(new StreamFixtureProvider()));
                await Error("permission_denied", () => restricted.MediaStreams!.Open(new("fixture", null), null, options));
            }
            var grant = new PermissionGrant(package, permissions); string area = Area();
            var firstProvider = new StreamFixtureProvider(); var secondProvider = new StreamFixtureProvider();
            await using var first = new Broker(package, grant, area, sessions: new SessionRegistry(firstProvider));
            await using var second = new Broker(package, grant, area, sessions: new SessionRegistry(secondProvider));
            var opened = first.MediaStreams!.Open(new("fixture", null), null, options); string id = opened["streamId"]!.GetValue<string>();
            string other = second.MediaStreams!.Open(new("fixture", null), null, options)["streamId"]!.GetValue<string>();
            await Until(() => firstProvider.Last is not null && secondProvider.Last is not null);
            await Error("stream_not_found", () => second.MediaStreams.Status(id));
            await Error("stream_not_found", () => second.MediaStreams.RequestClose(id));
            firstProvider.Last!.Failed = true;
            await Until(() => first.MediaStreams.Status(id)["state"]!.GetValue<string>() == "failed");
            True(firstProvider.Active == 0 && !Directory.Exists(firstProvider.Last.Directory));
            True(secondProvider.Active == 1 && Directory.Exists(secondProvider.Last!.Directory));
            await second.MediaStreams.CloseAsync(other, default);
            True(secondProvider.Active == 0 && !Directory.Exists(secondProvider.Last!.Directory));
        });
        await Test("Stream broker supports capacity-one replacement and retains completed media independently", async () =>
        {
            var package = Package(permissions: ["sessions.manage", "media.input", "media.output"]);
            var grant = new PermissionGrant(package, package.Manifest.Permissions); var provider = new StreamFixtureProvider();
            await using var broker = new Broker(package, grant, Area(), sessions: new SessionRegistry(provider, 1, 1));
            async Task<JsonNode?> Call(string method, JsonObject args) => await broker.InvokeAsync(method, args, default);
            JsonObject Options() => new() { ["sourceId"] = "fixture", ["encoding"] = new NativeEncoding("h264", "matroska", 1000).ToJson(),
                ["destination"] = new JsonObject { ["type"] = "servedStream" } };
            var first = await Call("outputs.open", Options());
            string id = first!["streamId"]!.GetValue<string>(), session = first["sessionId"]!.GetValue<string>();
            await Until(() => broker.MediaStreams!.Status(id)["state"]!.GetValue<string>() == "running");
            await Call("sessions.pause", new() { ["sessionId"] = session, ["paused"] = true });
            await Until(() => provider.Last!.Paused);
            await Error("operation_unavailable", () => Call("sessions.seek", new() { ["sessionId"] = session, ["seconds"] = 1.0 }));
            await Call("sessions.close", new() { ["sessionId"] = session });
            True(provider.Active == 0 && broker.MediaStreams!.Status(id)["nativeCapacityReleased"]!.GetValue<bool>());
            var second = await Call("outputs.open", Options()); string next = second!["streamId"]!.GetValue<string>();
            True(first["generationId"]!.GetValue<string>() != second["generationId"]!.GetValue<string>());
            await Until(() => broker.MediaStreams!.Status(next)["state"]!.GetValue<string>() == "running");
            provider.Last!.Complete = true;
            await Until(() => broker.MediaStreams!.Status(next)["nativeCapacityReleased"]!.GetValue<bool>());
            var page = broker.MediaStreams!.Segments(next, null, 32); True(page["segments"]!.AsArray().Count == 1 && provider.Active == 0);
            True(Directory.Exists(provider.Last.Directory));
            await Call("sessions.close", new() { ["sessionId"] = second["sessionId"]!.DeepClone() });
            True(!Directory.Exists(provider.Last.Directory));
        });
        await Test("Stream close cancels startup and cleanup failures retain ownership for retry", async () =>
        {
            var package = Package(permissions: ["sessions.manage", "media.input", "media.output"]);
            var grant = new PermissionGrant(package, package.Manifest.Permissions); var provider = new StreamFixtureProvider { SlowStart = true };
            await using var broker = new Broker(package, grant, Area(), sessions: new SessionRegistry(provider));
            JsonObject Open() => broker.MediaStreams!.Open(new("fixture", null), null, new(new("h264", "matroska", 1000), new(), true, 1));
            string id = Open()["streamId"]!.GetValue<string>(); await provider.Entered.Task;
            await broker.MediaStreams!.CloseAsync(id, default); True(provider.Active == 0);
            provider.SlowStart = false; id = Open()["streamId"]!.GetValue<string>();
            await Until(() => broker.MediaStreams.Status(id)["state"]!.GetValue<string>() == "running");
            provider.Last!.FailClose = true; broker.MediaStreams.RequestClose(id);
            await Until(() => broker.MediaStreams.Status(id)["state"]!.GetValue<string>() == "cleanupFailed");
            True(!broker.MediaStreams.Status(id)["nativeCapacityReleased"]!.GetValue<bool>() && provider.Active == 1);
            provider.Last.FailClose = false;
            await broker.MediaStreams.CloseAsync(id, default); True(provider.Active == 0 && !Directory.Exists(provider.Last.Directory));
        });
        await Test("Stream orphan recovery removes only unlocked owned caches and preserves live streams", async () =>
        {
            string area = Area(); await using var live = StreamCache.Create(area, 0);
            string orphan = Path.Combine(area, "stream-cache", "stream-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(orphan); File.WriteAllText(Path.Combine(orphan, ".owner"), "");
            File.WriteAllBytes(Path.Combine(orphan, "part_00000000.ts"), [1]);
            StreamCache.Recover(area); True(!Directory.Exists(orphan) && Directory.Exists(live.Directory));
            Directory.CreateDirectory(orphan); File.WriteAllText(Path.Combine(orphan, ".owner"), "");
            string unexpected = Path.Combine(orphan, "personal.txt"); File.WriteAllText(unexpected, "keep");
            await Error("invalid_stream_cache", () => StreamCache.Recover(area));
            True(File.ReadAllText(unexpected) == "keep" && File.Exists(Path.Combine(orphan, ".owner")));
        });
    }
    private sealed class StreamFixtureProvider : IProcessingSessionProvider, IMediaStreamProvider, IProcessingSelectionProvider
    {
        internal int Active; internal bool SlowStart; internal StreamFixtureSession? Last;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SupportsStreams => true;
        public JsonObject ListSelections() => new() { ["sources"] = new JsonArray(new JsonObject { ["id"] = "fixture", ["name"] = "Fixture" }),
            ["profiles"] = new JsonArray(new JsonObject { ["id"] = "profile", ["name"] = "Fixture profile" }) };
        public Task<IProcessingSession> OpenAsync(string sourceId, string? profileId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public async Task<IProcessingSession> OpenStreamAsync(ProbeRequest source, string? profile, StreamRequest output, string cacheDirectory, CancellationToken token)
        {
            Entered.TrySetResult(); if (SlowStart) await Task.Delay(Timeout.Infinite, token);
            if (Interlocked.CompareExchange(ref Active, 1, 0) != 0) throw new AddonException("capacity_exceeded", "Fixture has one encoder.");
            Last = new(this, cacheDirectory); return Last;
        }
    }
    private sealed class StreamFixtureSession(StreamFixtureProvider owner, string directory) : IMediaStreamProducer
    {
        internal readonly string Directory = directory;
        internal volatile bool Paused, Complete, FailClose, Failed;
        private bool disposed;
        public Task<JsonObject> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(new JsonObject
        { ["state"] = Failed ? "failed" : Complete ? "completed" : "running", ["buffer"] = new JsonObject { ["producedEndSeconds"] = 1.0 } });
        public Task SetDemandAsync(double position, CancellationToken token) => Task.CompletedTask;
        public Task PauseAsync(bool paused, CancellationToken cancellationToken) { Paused = paused; return Task.CompletedTask; }
        public Task SeekAsync(double seconds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync()
        {
            if (FailClose) throw new IOException("fixture close failure");
            if (!disposed)
            {
                // Deliberately finish the final index only during disposal.
                if (Complete) { File.WriteAllBytes(Path.Combine(Directory, "part_00000000.mkv"), [1, 2]); File.WriteAllText(Path.Combine(Directory, "index.csv"), "part_00000000.mkv,0,1\n"); }
                if (Complete) FixtureStreamMetadata(Directory, "part_00000000.mkv");
                disposed = true; Interlocked.Decrement(ref owner.Active);
            }
            return ValueTask.CompletedTask;
        }
    }
}
