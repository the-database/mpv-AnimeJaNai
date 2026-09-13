using System.Text.Json.Nodes;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Management;

internal static partial class Checks
{
    private static async Task LifecycleChecks()
    {
        static AddonPackage LifecyclePackage(string version = "1.0.0", string[]? activation = null)
        {
            var manifest = Manifest(version: version);
            manifest = manifest with { Activation = activation ?? ["manual", "on_manager", "on_player", "on_login"] };
            return AddonPackage.Create(manifest, EmptyModule);
        }
        static Task<JsonNode?> Hello(AddonService service, string id, string kind) => service.InvokeAsync(id,
            kind == "on_manager" ? "manager.hello" : "lifecycle.hello", new() { ["major"] = 1L, ["kind"] = kind }, default);
        await Test("Player, Manager and login triggers share one worker and release only their own activation", async () =>
        {
            string area = Area(); var package = LifecyclePackage(); new AddonRegistry(area).Install(package, []);
            var instances = new List<FakeAddon>();
            var login = TestLoginSettings(area, new MemoryLoginStore()); login.Update(true);
            await using var service = new AddonService(area, (_, _, _, _) => { var value = new FakeAddon(); instances.Add(value); return Task.FromResult<IAddonInstance>(value); }, loginSettings: login);
            await Hello(service, "player1", "on_player"); await Hello(service, "player2", "on_player");
            await Hello(service, "manager", "on_manager"); await Hello(service, "login", "on_login");
            True(instances.Count == 1 && instances[0].Events.SequenceEqual(new[] { "start" }));
            await service.DisconnectAsync("player1"); await service.DisconnectAsync("manager"); await service.DisconnectAsync("login");
            True(service.HasRunningWorkers);
            await service.DisconnectAsync("player2"); True(!service.HasRunningWorkers && instances[0].Events.Last() == "stop");
        });
        await Test("Lifecycle roles cannot switch to management and guests cannot send trusted lifecycle calls", async () =>
        {
            string area = Area(); await using var service = new AddonService(area, (_, _, _, _) => throw new NotSupportedException());
            await Error("handshake_required", () => service.InvokeAsync("unregistered", "lifecycle.ping", new(), default));
            await Error("invalid_activation", () => Hello(service, "bad", "manual"));
            await Hello(service, "player", "on_player"); await Hello(service, "player", "on_player");
            await Error("client_role_fixed", () => Hello(service, "player", "on_manager"));
            await Error("client_role_fixed", () => Hello(service, "player", "on_login"));
            foreach (string method in new[] { "addons.list", "addons.start", "addons.installDev", "host.configure", "network.selections" })
                await Error("management_denied", () => service.InvokeAsync("player", method, new(), default));
            True((await service.InvokeAsync("player", "lifecycle.ping", new(), default))!["connected"]!.GetValue<bool>());
            var package = Package(); await using var broker = new Broker(package, new(package, []), area);
            await Error("unknown_method", () => broker.InvokeAsync("lifecycle.hello", new() { ["major"] = 1L, ["kind"] = "on_player" }, default));
        });
        await Test("Player-only addons installed or rolled back during playback follow the existing player connection", async () =>
        {
            string area = Area(); var instances = new List<FakeAddon>();
            await using var service = new AddonService(area, (_, _, _, _) => { var value = new FakeAddon(); instances.Add(value); return Task.FromResult<IAddonInstance>(value); });
            await Hello(service, "player", "on_player"); await Hello(service, "manager", "on_manager");
            foreach (string version in new[] { "1.0.0", "1.1.0" })
            {
                var package = LifecyclePackage(version, ["on_player"]); string path = Path.Combine(area, version + ".ajnaddon"); package.Save(path);
                await service.InvokeAsync("manager", "addons.installDev", new() { ["path"] = path, ["expectedHash"] = package.Hash, ["permissions"] = new JsonArray() }, default);
                True(service.HasRunningWorkers);
            }
            await service.InvokeAsync("manager", "addons.rollback", new() { ["id"] = "org.example.test" }, default);
            True(instances.Count == 3 && instances.Take(2).All(i => i.IsStopped) && !instances[2].IsStopped);
            await service.DisconnectAsync("player"); True(!service.HasRunningWorkers, "The remaining Manager must not activate a player-only addon.");
        });
        await Test("A stopped addon stays stopped during lifecycle pings; manual work survives player exit", async () =>
        {
            string area = Area(); var package = LifecyclePackage(); new AddonRegistry(area).Install(package, []); int starts = 0;
            await using var service = new AddonService(area, (_, _, _, _) => { starts++; return Task.FromResult<IAddonInstance>(new FakeAddon()); });
            await Hello(service, "player", "on_player"); await Hello(service, "manager", "on_manager");
            await service.InvokeAsync("manager", "addons.stop", new() { ["id"] = package.Manifest.Id }, default);
            await service.InvokeAsync("player", "lifecycle.ping", new(), default); await Hello(service, "player", "on_player");
            True(!service.HasRunningWorkers && starts == 1);
            await service.InvokeAsync("manager", "addons.start", new() { ["id"] = package.Manifest.Id }, default);
            await service.DisconnectAsync("player"); await service.DisconnectAsync("manager");
            True(service.HasRunningWorkers && starts == 2);
        });
        if (OperatingSystem.IsWindows()) await Test("The actual private lifecycle client disconnects and releases player ownership", async () =>
        {
            string area = Area(); var package = LifecyclePackage(activation: ["on_player"]); new AddonRegistry(area).Install(package, []);
            var service = new AddonService(area, (_, _, _, _) => Task.FromResult<IAddonInstance>(new FakeAddon()));
            using var stop = new CancellationTokenSource(); var server = new ManagementServer(area, service).RunAsync(stop.Token, exitWhenIdle: false);
            try
            {
                await using var first = await ManagementClient.ConnectLifecycleAsync(area, "on_player");
                await using var second = await ManagementClient.ConnectLifecycleAsync(area, "on_player");
                await using var manager = await ManagementClient.ConnectAsync(area);
                True(service.HasRunningWorkers);
                try { await first.ListAsync(); throw new Exception("Player listing must be denied."); }
                catch (ManagementException error) when (error.Code == "management_denied") { }
                await first.CallAsync("lifecycle.ping"); await first.DisposeAsync();
                await second.CallAsync("lifecycle.ping"); True(service.HasRunningWorkers);
                await second.DisposeAsync();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (service.HasRunningWorkers) await Task.Delay(20, deadline.Token);
                True(!(await manager.ListAsync())[0]!["running"]!.GetValue<bool>());
            }
            finally { stop.Cancel(); await server.WaitAsync(TimeSpan.FromSeconds(10)); }
        });
    }
}
