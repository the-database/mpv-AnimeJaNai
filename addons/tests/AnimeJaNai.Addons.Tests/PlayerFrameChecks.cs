using System.Text.Json.Nodes;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Native;
using System.Security.Cryptography;

internal static partial class Checks
{
    private static async Task PlayerFrameChecks()
    {
        await Test("Player observations require distinct consent and do not grant session control", async () =>
        {
            using var registry = new PlayerFrameRegistry();
            using var registered = registry.Register(new PlayerSource(), out string playerId);
            var package = Package(permissions: ["frames.read", "player.observe", "sessions.manage"]);
            foreach (var permission in new[] { Array.Empty<string>(), new[] { "frames.read", "sessions.manage" }, new[] { "player.observe" } })
            {
                await using var denied = new Broker(package, new(package, permission), Area(), playerFrames: registry);
                await Error("permission_denied", () => denied.InvokeAsync("playerFrames.list", new(), default));
                await Error("permission_denied", () => denied.InvokeTransportAsync("playerFrames.read", new() { ["subscriptionId"] = "guess" }, default));
            }
            await using var first = new Broker(package, new(package, ["frames.read", "player.observe"]), Area(), playerFrames: registry);
            await using var second = new Broker(package, new(package, ["frames.read", "player.observe"]), Area(), playerFrames: registry);
            True(first.Info()["capabilities"]!["playerFrames"] is not null && first.Info()["capabilities"]!["sessions"] is null);
            var list = (JsonArray)(await first.InvokeAsync("playerFrames.list", new(), default))!;
            True(list.Count == 1 && ((JsonObject)list[0]!).Count == 3 && list[0]!["playerId"]!.GetValue<string>() == playerId);
            string id = (await first.InvokeAsync("playerFrames.subscribe", PlayerRequest(playerId), default))!["subscriptionId"]!.GetValue<string>();
            True((await first.InvokeTransportAsync("playerFrames.read", new() { ["subscriptionId"] = id }, default)).Binary.Length == 4);
            await Error("subscription_not_found", () => second.InvokeTransportAsync("playerFrames.read", new() { ["subscriptionId"] = id }, default));
            await Error("permission_denied", () => first.InvokeAsync("sessions.pause", new() { ["sessionId"] = playerId, ["paused"] = true }, default));
            await Error("unknown_method", () => first.InvokeAsync("player.attach", new() { ["processId"] = 1 }, default));
            await first.DisposeAsync();
            await Error("owner_closed", () => first.InvokeTransportAsync("playerFrames.read", new() { ["subscriptionId"] = id }, default));
        });
        await Test("Player consumers share one producer with independent dimensions, rates and frame identities", () =>
        {
            var clock = new ManualClock(); using var registry = new PlayerFrameRegistry(clock);
            var source = new PlayerSource(); using var registration = registry.Register(source, out string player);
            var slow = registry.CreateOwner(); var fast = registry.CreateOwner();
            string small = registry.Subscribe(slow, player, new(1, 1, 10))["subscriptionId"]!.GetValue<string>();
            string large = registry.Subscribe(fast, player, new(2, 2, 60))["subscriptionId"]!.GetValue<string>();
            True(source.Active == new FrameRequest(2, 2, 60) && source.ActiveLeases == 1);
            var tiny = registry.Read(slow, small)!; var big = registry.Read(fast, large)!;
            True(tiny.Pixels.Length == 4 && tiny.Pixels.Span[0] == 30 && tiny.Pixels.Span[3] == 255);
            True(big.Pixels.Length == 16 && big.Pixels.Span[0] == 0 && big.Pixels.Span[12] == 60);
            source.Id = 2; clock.Now = 17;
            True(registry.Read(fast, large) is not null && registry.Read(slow, small) is null);
            clock.Now = 110; True(registry.Read(slow, small) is not null);
            clock.Now = 220; True(registry.Read(slow, small) is null);
            registry.ReleaseOwner(fast); True(source.Active == new FrameRequest(1, 1, 10) && !source.Closed);
            source.Id = 3; clock.Now = 330; True(registry.Read(slow, small) is not null);
            registry.Unsubscribe(slow, small); True(source.ActiveLeases == 0 && source.Active is null && !source.Closed);
        });
        await Test("Player invalidation, close and failed subscription reconfiguration preserve other observers", async () =>
        {
            var clock = new ManualClock(); using var registry = new PlayerFrameRegistry(clock);
            var source = new PlayerSource(); using var registration = registry.Register(source, out string player);
            var owner = registry.CreateOwner(); string id = registry.Subscribe(owner, player, new(1, 1, 30))["subscriptionId"]!.GetValue<string>();
            True(registry.Read(owner, id) is not null);
            source.Ready = false; source.Epoch++; clock.Now = 100; True(registry.Read(owner, id) is null);
            source.Ready = true; clock.Now = 200; True(registry.Read(owner, id)!.Metadata["epoch"]!.GetValue<string>() == "2");
            source.FailNext = true;
            try { registry.Subscribe(owner, player, new(2, 2, 60)); throw new Exception("Expected provider failure"); }
            catch (IOException) { }
            True(source.Active == new FrameRequest(1, 1, 30) && source.ActiveLeases == 1);
            source.Id++; clock.Now = 300; True(registry.Read(owner, id) is not null);
            registration.Dispose(); True(source.Closed && source.ActiveLeases == 0 && registry.List(owner).Count == 0);
            await Error("player_closed", () => registry.Read(owner, id));
            registry.Unsubscribe(owner, id);
            await Error("subscription_not_found", () => registry.Read(owner, id));
        });
        await Test("Player and subscriber limits hold across owners and release correctly", async () =>
        {
            using var registry = new PlayerFrameRegistry();
            using var registration = registry.Register(new PlayerSource(), out string player);
            var first = registry.CreateOwner(); var second = registry.CreateOwner(); var third = registry.CreateOwner();
            for (int i = 0; i < 4; i++) registry.Subscribe(first, player, new());
            await Error("capacity_exceeded", () => registry.Subscribe(first, player, new()));
            for (int i = 0; i < 4; i++) registry.Subscribe(second, player, new());
            await Error("capacity_exceeded", () => registry.Subscribe(third, player, new()));
            registry.ReleaseOwner(first); registry.Subscribe(third, player, new());
            for (int i = 0; i < 3; i++) registry.Register(new PlayerSource(), out _);
            using var extra = new PlayerSource();
            await Error("capacity_exceeded", () => registry.Register(extra, out _));
            await Error("invalid_request", () => registry.Subscribe(third, player, new(321, 1, 1)));
            registration.Dispose(); registry.Register(extra, out _);
        });
        await Test("Player bridge operations stay unavailable on guest and Manager roles", async () =>
        {
            await using var service = new AddonService(Area(), (_, _, _, _) => throw new Exception("No worker expected"));
            await service.InvokeAsync("player", "lifecycle.hello", new() { ["major"] = 1L, ["kind"] = "on_player" }, default);
            True((await service.InvokeAsync("player", "player.attach", new() { ["processId"] = 1L }, default))!["available"]!.GetValue<bool>() == false);
            await service.InvokeAsync("manager", "manager.hello", new() { ["major"] = 1L }, default);
            await Error("unknown_method", () => service.InvokeAsync("manager", "player.attach", new(), default));
            await Error("management_denied", () => service.InvokeAsync("player", "addons.list", new(), default));
        });
        if (OperatingSystem.IsWindows()) await Test("Player observation requires both exact native binaries and its private ABI marker", () =>
        {
            string area = Area(); Directory.CreateDirectory(Path.Combine(area, "addon-host"));
            File.WriteAllBytes(Path.Combine(area, "mpv.exe"), [1, 2, 3]); File.WriteAllBytes(Path.Combine(area, "libmpv-2.dll"), [4, 5, 6]);
            True(!NativePlayerObservations.Available(area));
            JsonObject marker = new() { ["privatePlayerSampleAbi"] = 1L,
                ["playerSha256"] = Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1, 2, 3 })),
                ["librarySha256"] = Convert.ToHexStringLower(SHA256.HashData(new byte[] { 4, 5, 6 })) };
            File.WriteAllText(Path.Combine(area, "addon-host", "native-player-frames.json"), marker.ToJsonString());
            True(NativePlayerObservations.Available(area)); File.WriteAllBytes(Path.Combine(area, "mpv.exe"), [3, 2, 1]);
            True(!NativePlayerObservations.Available(area));
        });
    }

    private static JsonObject PlayerRequest(string player) => new() { ["playerId"] = player, ["stage"] = "processed", ["format"] = "bgra8",
        ["width"] = 1L, ["height"] = 1L, ["maxFps"] = 30L };
    private sealed class PlayerSource : IPlayerFrameSource
    {
        public FrameRequest? Active;
        public int ActiveLeases;
        public bool Ready = true, Closed, FailNext;
        public long Id = 1, Epoch = 1;
        public IFrameSubscription Subscribe(FrameRequest request)
        {
            True(!Closed && ActiveLeases == 0);
            if (FailNext) { FailNext = false; throw new IOException("Intentional provider failure"); }
            Active = request; ActiveLeases++;
            return new Samples(this, request);
        }
        public void Dispose() { Closed = true; }
        private sealed class Samples(PlayerSource source, FrameRequest request) : IFrameSubscription
        {
            private bool closed;
            public FramePacket? ReadLatest()
            {
                True(!closed && !source.Closed);
                if (!source.Ready) return null;
                byte[] pixels = new byte[request.Width * request.Height * 4];
                for (int y = 0; y < request.Height; y++) for (int x = 0; x < request.Width; x++)
                { pixels[(y * request.Width + x) * 4] = (byte)(x * 20 + y * 40); pixels[(y * request.Width + x) * 4 + 3] = 255; }
                return new(new JsonObject { ["frameId"] = source.Id.ToString(), ["epoch"] = source.Epoch.ToString(),
                    ["width"] = (long)request.Width, ["height"] = (long)request.Height, ["format"] = "bgra8", ["stage"] = "processed" }, pixels);
            }
            public void Dispose() { if (!closed) { closed = true; source.ActiveLeases--; source.Active = null; } }
        }
    }
}
