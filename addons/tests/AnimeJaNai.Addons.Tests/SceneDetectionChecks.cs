using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Native;

internal static partial class Checks
{
    private static async Task SceneDetectionChecks()
    {
        await Test("Scene detection requires separate consent and exclusive owned attachments", async () =>
        {
            using var registry = new SceneDetectionRegistry();
            using var source = new SceneFixture(); using var registration = registry.Register("player", source);
            var package = Package(permissions: ["frames.read", "player.sceneDetection", "player.observe"]);
            await using var denied = new Broker(package, new(package, ["frames.read", "player.observe"]), Area(), sceneDetection: registry);
            await Error("permission_denied", () => denied.InvokeAsync("sceneDetection.list", new(), default));
            await Error("permission_denied", () => denied.InvokeTransportAsync("sceneDetection.read", new() { ["detectorId"] = "guess" }, default));
            await using var first = new Broker(package, new(package, ["frames.read", "player.sceneDetection"]), Area(), sceneDetection: registry);
            await using var second = new Broker(package, new(package, ["frames.read", "player.sceneDetection"]), Area(), sceneDetection: registry);
            True(first.Info()["capabilities"]!["sceneDetection"] is not null);
            JsonObject Request() => new() { ["playerId"] = "player", ["width"] = 2L, ["height"] = 1L, ["deadlineMs"] = 25L };
            var attached = await first.InvokeAsync("sceneDetection.attach", Request(), default);
            string id = attached!["detectorId"]!.GetValue<string>();
            await Error("scene_detector_in_use", () => second.InvokeAsync("sceneDetection.attach", Request(), default));
            await Error("scene_detector_not_found", () => second.InvokeAsync("sceneDetection.status", new() { ["detectorId"] = id }, default));
            var oversized = Request(); oversized["width"] = long.MaxValue;
            await Error("invalid_request", () => first.InvokeAsync("sceneDetection.attach", oversized, default));
            for (int i = 0; i < 100; i++) source.Ready!();
            True(first.TakeSceneEvent()?["detectorId"]?.GetValue<string>() == id && first.TakeSceneEvent() is null);
            var read = await first.InvokeTransportAsync("sceneDetection.read", new() { ["detectorId"] = id }, default);
            True(read.Binary.Span.SequenceEqual(new byte[] { 0, 0, 255, 255 }));
            foreach (string decision in new[] { "cut", "continuous", "default" })
            {
                var result = await first.InvokeAsync("sceneDetection.submit", new() { ["detectorId"] = id, ["requestId"] = "1", ["decision"] = decision }, default);
                True(result?["accepted"]?.GetValue<bool>() == true);
            }
            True(source.Decisions.SequenceEqual(new[] { 1, 0, -1 }));
            await first.DisposeAsync(); True(source.Released);
            True((await second.InvokeAsync("sceneDetection.attach", Request(), default))?["detectorId"] is not null);
        });
        if (!OperatingSystem.IsWindows()) return;
        await Test("Scene detection native mapping rejects stale late duplicate and revoked decisions", async () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            using var buffer = new NativeSceneBuffer(); buffer.RenewLease();
            using var mapping = MemoryMappedFile.OpenExisting(buffer.Name);
            using var view = mapping.CreateViewAccessor(0, NativeSceneBuffer.Size);
            using var lease = buffer.Subscribe(new(2, 1, 25), () => { });
            PublishScene(view, 1, 2, 1, true);
            var pair = lease.Read(); True(pair?.Pixels.Span.SequenceEqual(new byte[] { 0, 0, 255, 255 }) == true);
            True(lease.Read() is null && lease.Submit("1", 1) && !lease.Submit("1", 0));
            True(view.ReadInt32(104) == 1);
            PublishScene(view, 2, 2, 1, false);
            True(!lease.Submit("1", 1) && lease.Submit("2", 0));
            True(view.ReadInt32(104) == 0);
            PublishScene(view, 3, 2, 1, false);
            view.Write(64, Environment.TickCount64 - 1);
            True(lease.Read() is null && !lease.Submit("3", 1) && view.ReadInt64(48) == 0);
            PublishScene(view, 4, 2, 1, false); view.Write(56, Environment.TickCount64 - 1);
            True(lease.Read() is null && !lease.Submit("4", 1)); buffer.RenewLease();
            PublishScene(view, 5, 2, 1, false); view.Write(32, 0L); view.Write(128, 2L);
            True(!lease.Submit("5", 1));
            view.Write(16, 1); lease.Dispose(); True(view.ReadInt32(20) == 0); view.Write(16, 0);
            await Error("scene_detector_not_found", () => lease.Submit("5", 1));
            using var next = buffer.Subscribe(new(2, 1, 25), () => { });
            True(!next.Submit("5", 1));
        });
    }
    private static void PublishScene(MemoryMappedViewAccessor view, long id, int width, int height, bool cut)
    {
        // Synthetic producer only: a long deadline avoids coupling protocol
        // assertions to test-machine scheduling. Native deadline tests are
        // separate; production producers are limited to 5..100 milliseconds.
        view.Write(16, 1); view.Write(32, id); view.Write(48, 0L);
        view.Write(64, Environment.TickCount64 + 5000); view.Write(72, (double)id); view.Write(80, id + .04);
        view.Write(88, 1920); view.Write(92, 1080); view.Write(96, width); view.Write(100, height);
        view.Write(108, 1); view.Write(128, 1L);
        byte[] pixels = new byte[width * height * 2];
        if (cut) Array.Fill(pixels, (byte)255, width * height, width * height);
        view.WriteArray(NativeSceneBuffer.HeaderBytes, pixels, 0, pixels.Length); view.Write(16, 0);
    }
    private sealed class SceneFixture : ISceneSource
    {
        internal Action? Ready;
        internal bool Released;
        internal readonly List<int> Decisions = [];
        public ISceneSubscription Subscribe(SceneRequest request, Action ready) { Ready = ready; return new Subscription(this); }
        public void Dispose() { Released = true; }
        private sealed class Subscription(SceneFixture source) : ISceneSubscription
        {
            public ScenePair? Read() => new(new() { ["width"] = 2L, ["height"] = 1L, ["requestId"] = "1" }, new byte[] { 0, 0, 255, 255 });
            public bool Submit(string id, int decision) { source.Decisions.Add(decision); return true; }
            public JsonObject Status() => new() { ["state"] = "active" };
            public void Dispose() { source.Released = true; }
        }
    }
    private static async Task SceneRuntimeChecks(string runtime, string compiler, string dotnet)
    {
        if (!OperatingSystem.IsWindows()) return;
        await Test("Scene detection compiled Wasm addon handles real binary pairs and releases its native lease", async () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            string area = Area();
            var package = await DeveloperTools.BuildAsync(Path.Combine(AppContext.BaseDirectory, "scene-detector"), compiler, Path.Combine(area, "scene.ajnaddon"));
            using var registry = new SceneDetectionRegistry();
            using var buffer = new NativeSceneBuffer(); buffer.RenewLease();
            using var registration = registry.Register("fixture-player", buffer);
            using var mapping = MemoryMappedFile.OpenExisting(buffer.Name);
            using var view = mapping.CreateViewAccessor(0, NativeSceneBuffer.Size);
            using var signal = EventWaitHandle.OpenExisting(buffer.Name + ".Ready");
            await using (var worker = await AddonWorker.StartAsync(package, new(package, package.Manifest.Permissions), runtime, Path.Combine(area, "workers"),
                Path.Combine(area, "data"), new WorkerCommand(Path.GetFullPath(dotnet), [typeof(AddonWorker).Assembly.Location]), sceneDetection: registry))
            {
                // The production manager sends start before actions. The
                // example initializes its cached detector settings there.
                await worker.SendEventAsync("start");
                var attached = await worker.SendEventAsync("action", new JsonObject { ["id"] = "attach" });
                True(attached?["attached"]?.GetValue<int>() == 1);
                foreach (int id in new[] { 1, 2 })
                {
                    buffer.RenewLease(); PublishScene(view, id, 160, 90, id == 1);
                    long start = Stopwatch.GetTimestamp(); signal.Set();
                    await Until(() => view.ReadInt64(48) == id);
                    True(view.ReadInt32(104) == (id == 1 ? 1 : 0), worker.Diagnostics);
                    Console.WriteLine($"Scene pair {id} response: {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2} ms (synthetic producer).");
                }
            }
            True(view.ReadInt32(20) == 0, "Closing the addon did not revoke scene processing.");
        });
    }
}
