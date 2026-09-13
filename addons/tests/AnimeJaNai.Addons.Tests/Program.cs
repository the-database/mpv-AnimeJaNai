using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Management;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

return await Checks.RunAsync(args);

internal static partial class Checks
{
    private static readonly List<object> results = [];
    private static string root = "";
    private static int failed;
    private static readonly byte[] EmptyModule = [0, 97, 115, 109, 1, 0, 0, 0];
    private static string Area() => Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N"))).FullName;
    private static AddonManifest Manifest(string id = "org.example.test", string version = "1.0.0", string[]? permissions = null) => new()
    {
        SchemaVersion = 1, Id = id, Name = "Test addon", Version = version,
        Api = new() { Major = 1, MinMinor = 0 }, ModuleSha256 = new string('0', 64), Permissions = permissions ?? [],
    };
    private static AddonPackage Package(string id = "org.example.test", string version = "1.0.0", string[]? permissions = null) =>
        AddonPackage.Create(Manifest(id, version, permissions), EmptyModule);
    private static void True(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
    private static void Same(JsonNode? expected, JsonNode? actual) => True(JsonNode.DeepEquals(expected, actual), $"Expected {expected}; received {actual}");
    private static async Task Error(string code, Func<Task> action)
    {
        try { await action(); }
        catch (AddonException e) when (e.Code == code) { return; }
        throw new Exception("Expected AddonException: " + code);
    }
    private static Task Error(string code, Action action) => Error(code, () => { action(); return Task.CompletedTask; });
    private static async Task Test(string name, Func<Task> action)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            await action();
            results.Add(new { name, passed = true, milliseconds = timer.Elapsed.TotalMilliseconds });
            Console.WriteLine("PASS " + name);
        }
        catch (Exception error)
        {
            failed++;
            results.Add(new { name, passed = false, milliseconds = timer.Elapsed.TotalMilliseconds, error = error.ToString() });
            Console.WriteLine("FAIL " + name + ": " + error);
        }
    }
    private static Task Test(string name, Action action) => Test(name, () => { action(); return Task.CompletedTask; });

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length is not (1 or 4)) { Console.WriteLine("Tests <output-directory> [wasmtime.exe javy.exe dotnet.exe]"); return 2; }
        root = Directory.CreateDirectory(Path.Combine(Path.GetFullPath(args[0]), "run-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"))).FullName;
        await PackageChecks();
        await PersistenceChecks();
        await SettingsAndActivationChecks();
        await ChannelChecks();
        await SessionChecks();
        await MediaSelectionChecks();
        await FrameAndTimerChecks();
        await NetworkChecks();
        await EncodingChecks();
        await OutputChecks();
        await HostSettingsChecks();
        if (OperatingSystem.IsWindows()) await ManagementChecks();
        if (args.Length == 4) await RuntimeChecks(args[1], args[2], args[3]);
        if (args.Length == 4) await FrameRuntimeChecks(args[1], args[2], args[3]);
        if (args.Length == 4) await NetworkRuntimeChecks(args[1], args[2], args[3]);
        if (args.Length == 4) await ServiceInspectorChecks(args[1], args[2], args[3]);
        if (args.Length == 4) await OutputRuntimeChecks(args[1], args[2], args[3]);
        string report = Path.Combine(root, "results.json");
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { passed = results.Count - failed, failed, results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{results.Count - failed} passed; {failed} failed. {report}");
        return failed == 0 ? 0 : 1;
    }

    private static byte[] Zip(params (string Name, byte[] Bytes, int Attributes)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            foreach (var item in entries)
            {
                var entry = zip.CreateEntry(item.Name); entry.ExternalAttributes = item.Attributes;
                using var stream = entry.Open(); stream.Write(item.Bytes);
            }
        return buffer.ToArray();
    }
    private static byte[] ManifestBytes(AddonManifest? manifest = null) => JsonSerializer.SerializeToUtf8Bytes(
        (manifest ?? Manifest()) with { ModuleSha256 = Convert.ToHexStringLower(SHA256.HashData(EmptyModule)) }, Contract.Json);

    private static async Task PackageChecks()
    {
        await Test("Package round trip and immutable permission snapshot", () =>
        {
            var original = Package(permissions: ["log.write"]);
            var copy = original.Manifest; copy.Permissions[0] = "storage.write";
            True(original.Manifest.Permissions.SequenceEqual(["log.write"]));
            string file = Path.Combine(Area(), "p.ajnaddon"); original.Save(file);
            True(AddonPackage.Load(file).Hash == original.Hash);
        });
        await Test("Package rejects module integrity mismatch", () => Error("integrity_mismatch", () =>
            AddonPackage.Read(Zip(("manifest.json", ManifestBytes(), 0), ("module.wasm", [.. EmptyModule, 0], 0)))));
        await Test("Package rejects extra, duplicate, nested and link entries", async () =>
        {
            foreach (string name in new[] { "extra", "manifest.json", "../module.wasm", "folder/module.wasm" })
                await Error("invalid_package", () => AddonPackage.Read(Zip(("manifest.json", ManifestBytes(), 0), (name, EmptyModule, 0))));
            await Error("invalid_package", () => AddonPackage.Read(Zip(("manifest.json", ManifestBytes(), 0), ("module.wasm", EmptyModule, unchecked((int)0xA0000000)))));
        });
        await Test("Package rejects compressed oversized module before execution", () => Error("package_too_large", () =>
            AddonPackage.Read(Zip(("manifest.json", ManifestBytes(), 0), ("module.wasm", new byte[AddonPackage.MaxModuleBytes + 1], 0)))));
        await Test("Package accepts only portable core Wasm", async () =>
        {
            await Error("invalid_module", () => AddonPackage.Create(Manifest(), [77, 90, 0, 0, 0, 0, 0, 0]));
            await Error("invalid_module", () => AddonPackage.Create(Manifest(), [0, 97, 115, 109, 13, 0, 1, 0]));
        });
        await Test("Manifest compatibility and optional metadata", async () =>
        {
            await Error("incompatible_api", () => (Manifest() with { Api = new() { Major = 2, MinMinor = 0 } }).Validate());
            (Manifest() with { Api = new() { Major = 1, MinMinor = Contract.Minor } }).Validate();
            await Error("incompatible_api", () => (Manifest() with { Api = new() { Major = 1, MinMinor = Contract.Minor + 1 } }).Validate());
            await Error("invalid_manifest", () => Manifest(permissions: ["files.write"]).Validate());
            var obj = Contract.ParseObject(ManifestBytes()); obj["futureOptionalMetadata"] = new JsonObject { ["description"] = "accepted" };
            True(AddonPackage.Read(Zip(("manifest.json", Encoding.UTF8.GetBytes(obj.ToJsonString()), 0), ("module.wasm", EmptyModule, 0))).Manifest.Metadata!.ContainsKey("futureOptionalMetadata"));
        });
        await Test("Permissions remain bound to exact package and declaration", async () =>
        {
            var first = Package(permissions: ["storage.read"]); var grant = new PermissionGrant(first, ["storage.read"]);
            await Error("permission_denied", () => grant.Demand("storage.write"));
            await Error("invalid_grant", () => new PermissionGrant(first, ["storage.write"]));
            await Error("invalid_grant", () => new Broker(Package(version: "1.0.1"), grant, Area()));
            var capabilities = AddonPackage.Create(Manifest() with { RequiredCapabilities = new() { ["frames"] = new() { Major = 1, MinMinor = 0 } } }, EmptyModule);
            await Error("missing_capability", () => new Broker(capabilities, new(capabilities, []), Area()));
        });
    }

    private static async Task PersistenceChecks()
    {
        await Test("Storage Unicode, nested values, null and addon isolation survive reload", () =>
        {
            string directory = Area(); var store = new AddonStorage(directory, "org.example.one");
            var value = JsonNode.Parse("""{"text":"日本語 🎬","bool":true,"array":[1,null,{"x":0.25}]}""");
            store.Set("config", value); store.Set("null", null);
            Same(value, new AddonStorage(directory, "org.example.one").Get("config"));
            True(new AddonStorage(directory, "org.example.two").Get("config") is null);
            var read = store.Get("config")!; read["text"] = "changed"; Same(value, store.Get("config"));
        });
        await Test("Storage key and size failures preserve saved data", async () =>
        {
            var store = new AddonStorage(Area(), "org.example.one"); store.Set("value", JsonValue.Create(5));
            await Error("invalid_key", () => store.Set("../other", null));
            await Error("storage_quota", () => store.Set("value", JsonValue.Create(new string('x', AddonStorage.MaxValueBytes))));
            Same(JsonValue.Create(5), store.Get("value"));
            for (int i = 0; i < 255; i++) store.Set("k" + i, null);
            await Error("storage_quota", () => store.Set("overflow", null));
            store.Set("value", JsonValue.Create(6)); Same(JsonValue.Create(6), store.Get("value"));
        });
        await Test("Corrupted stored data is preserved rather than overwritten", async () =>
        {
            string directory = Area(); var store = new AddonStorage(directory, "org.example.one");
            string file = Path.Combine(directory, "data", "org.example.one", "state.json");
            File.WriteAllText(file, "{broken");
            await Error("invalid_storage", () => store.Set("k", null)); True(File.ReadAllText(file) == "{broken");
        });
        await Test("Install update resets grants, rollback restores grants and retains data", async () =>
        {
            string directory = Area(); var registry = new AddonRegistry(directory);
            var first = Package(permissions: ["storage.read"]); var second = Package(version: "1.1.0", permissions: ["storage.read", "storage.write"]);
            registry.Install(first, ["storage.read"]); new AddonStorage(directory, first.Manifest.Id).Set("k", JsonValue.Create(7));
            registry.Install(second, []); True(registry.Load(first.Manifest.Id).Grant.Allowed.Count == 0);
            registry.Rollback(first.Manifest.Id); var loaded = registry.Load(first.Manifest.Id);
            True(loaded.Package.Hash == first.Hash && loaded.Grant.Allowed.SequenceEqual(["storage.read"]));
            Same(JsonValue.Create(7), new AddonStorage(directory, first.Manifest.Id).Get("k"));
            registry.Disable(first.Manifest.Id); await Error("not_installed", () => registry.Load(first.Manifest.Id));
        });
        await Test("Rejected update leaves active package usable", async () =>
        {
            var registry = new AddonRegistry(Area()); var first = Package(); registry.Install(first, []);
            await Error("invalid_grant", () => registry.Install(Package(version: "1.0.1"), ["storage.write"]));
            True(registry.Load(first.Manifest.Id).Package.Hash == first.Hash);
        });
        await Test("Modified installed package and null registration fail cleanly", async () =>
        {
            string directory = Area(); var registry = new AddonRegistry(directory); var first = Package(); registry.Install(first, []);
            string installed = Path.Combine(directory, "installed", first.Manifest.Id);
            Package(version: "1.0.1").Save(Path.Combine(installed, first.Hash + ".ajnaddon"));
            await Error("integrity_mismatch", () => registry.Load(first.Manifest.Id));
            File.WriteAllText(Path.Combine(installed, "active.json"), "{\"current\":null}");
            await Error("invalid_registration", () => registry.Load(first.Manifest.Id));
        });
        await Test("Broker denies direct calls without permissions", async () =>
        {
            var package = Package(permissions: ["storage.read", "storage.write", "sessions.manage"]);
            await using var broker = new Broker(package, new(package, []), Area());
            await Error("permission_denied", () => broker.InvokeAsync("storage.set", new() { ["key"] = "k", ["value"] = 1 }, default));
            await Error("permission_denied", () => broker.InvokeAsync("sessions.open", new() { ["sourceId"] = "chosen-source" }, default));
            await Error("unknown_method", () => broker.InvokeAsync("unrecognized.method", [], default));
            await using var allowed = new Broker(package, new(package, ["sessions.manage"]), Area());
            await Error("feature_unavailable", () => allowed.InvokeAsync("sessions.open", new() { ["sourceId"] = "chosen-source" }, default));
        });
    }

    private static async Task ChannelChecks()
    {
        await Test("Wire preserves split multibyte UTF-8 and buffered next message", async () =>
        {
            using var input = new ChunkedStream(Encoding.UTF8.GetBytes("{\"v\":\"日本語 🎬\"}\n{\"n\":2}\n"), 1);
            var channel = new MessageChannel(input, Stream.Null);
            True((await channel.ReadAsync(default))["v"]!.GetValue<string>() == "日本語 🎬");
            True((await channel.ReadAsync(default))["n"]!.GetValue<int>() == 2);
        });
        await Test("Wire rejects oversize, invalid UTF-8, duplicate keys and arrays", async () =>
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', Contract.MaxMessageBytes + 1) + "\n"));
            await Error("message_too_large", () => new MessageChannel(input, Stream.Null).ReadAsync(default));
            await Error("invalid_message", () => Contract.ParseObject([123, 34, 120, 34, 58, 34, 255, 34, 125]));
            await Error("invalid_message", () => Contract.ParseObject("{\"a\":{\"x\":1,\"x\":2}}"u8));
            await Error("invalid_message", () => Contract.ParseObject("[]"u8));
        });
    }

    private static AddonManifest SettingsManifest(string version = "1.0.0") => Manifest(version: version) with
    {
        Activation = ["manual", "on_manager", "on_player"],
        Settings = new()
        {
            ["enabled"] = new() { Type = "boolean", Label = "Enabled", Default = JsonValue.Create(true)! },
            ["rate"] = new() { Type = "number", Label = "Rate", Default = JsonValue.Create(20)!, Minimum = 1, Maximum = 60 },
            ["mode"] = new() { Type = "choice", Label = "Mode", Default = JsonValue.Create("normal")!, Choices = ["normal", "quiet"] },
            ["name"] = new() { Type = "string", Label = "Name", Default = JsonValue.Create("default")!, MaxLength = 100 },
        },
        Actions = new() { ["check"] = new() { Label = "Check status" } },
    };

    private static async Task SettingsAndActivationChecks()
    {
        await Test("Declarative settings defaults, type bounds and reload", async () =>
        {
            string directory = Area(); var manifest = SettingsManifest();
            var store = new AddonSettings(directory, manifest);
            Same(JsonValue.Create(20), store.Get()["rate"]);
            var patch = new JsonObject { ["rate"] = 30, ["enabled"] = false, ["name"] = "日本語 🎬", ["mode"] = "quiet" };
            store.Update(patch); Same(patch, new AddonSettings(directory, manifest).Get());
            await Error("invalid_settings", () => store.Update(new() { ["rate"] = 61 }));
            await Error("invalid_settings", () => store.Update(new() { ["enabled"] = "false" }));
            await Error("invalid_settings", () => store.Update(new() { ["mode"] = "other" }));
            await Error("invalid_settings", () => store.Update(new() { ["unknown"] = "other" }));
            Same(patch, store.Get());
        });
        await Test("Settings update/rollback preserves removed keys and rejects incompatible values", async () =>
        {
            string directory = Area(); var first = SettingsManifest();
            var original = new AddonSettings(directory, first); original.Update(new() { ["rate"] = 30, ["mode"] = "quiet" });
            var reduced = first with { Version = "2.0.0", Settings = new() { ["rate"] = first.Settings!["rate"] } };
            var upgraded = new AddonSettings(directory, reduced); upgraded.Update(new() { ["rate"] = 25 });
            True(!upgraded.Get().ContainsKey("mode")); Same(JsonValue.Create("quiet"), original.Get()["mode"]);
            var incompatible = reduced with { Settings = new() { ["rate"] = reduced.Settings!["rate"] with { Maximum = 22 } } };
            var narrowed = new AddonSettings(directory, incompatible);
            await Error("settings_incompatible", () => narrowed.Get());
            var editable = narrowed.GetForEditing();
            True(editable.Invalid.SequenceEqual(["rate"]));
            Same(JsonValue.Create(20), editable.Values["rate"]);
            Same(JsonValue.Create(25), original.Get()["rate"]);
            narrowed.Update(new() { ["rate"] = 21 }); Same(JsonValue.Create(21), narrowed.Get()["rate"]);
        });
        await Test("Manifest copies isolate settings definitions and defaults", () =>
        {
            var package = AddonPackage.Create(SettingsManifest(), EmptyModule);
            var view = package.Manifest; view.Settings!["mode"].Choices![0] = "changed"; view.Activation![0] = "changed";
            view.Settings.Clear(); True(package.Manifest.Settings!.Count == 4 && package.Manifest.Activation![0] == "manual");
        });
        await Test("Activation consolidates sources and stops only after the last source closes", async () =>
        {
            var package = AddonPackage.Create(SettingsManifest(), EmptyModule); var instances = new List<FakeAddon>();
            await using var controller = new AddonActivation(package, _ => { var addon = new FakeAddon(); instances.Add(addon); return Task.FromResult<IAddonInstance>(addon); });
            await controller.AcquireAsync("on_manager", "window");
            await Task.WhenAll(controller.AcquireAsync("on_player", "one"), controller.AcquireAsync("on_player", "one"), controller.AcquireAsync("on_player", "two"));
            True(instances.Count == 1 && instances[0].Events.Count == 1);
            await controller.ReleaseAsync("on_manager", "window"); await controller.ReleaseAsync("on_player", "one");
            True(!instances[0].IsStopped);
            await controller.ReleaseAsync("on_player", "two"); True(instances[0].IsStopped && instances[0].Events.Last() == "stop");
            await controller.AcquireAsync("manual", "button"); True(instances.Count == 2);
            await Error("activation_denied", () => controller.AcquireAsync("on_login", "login"));
        });
        await Test("Actions and settings changes use the existing activation without extra workers", async () =>
        {
            var package = AddonPackage.Create(SettingsManifest(), EmptyModule); var instances = new List<FakeAddon>();
            await using var controller = new AddonActivation(package, _ => { var addon = new FakeAddon(); instances.Add(addon); return Task.FromResult<IAddonInstance>(addon); });
            await controller.RunActionAsync("check"); True(instances[0].IsStopped && instances[0].Events.SequenceEqual(["start", "action", "stop"]));
            await controller.AcquireAsync("manual", "button"); await controller.RunActionAsync("check");
            await controller.UpdateSettingsAsync(new(Area(), package.Manifest), new() { ["rate"] = 25 });
            True(instances.Count == 2 && instances[1].Events.SequenceEqual(["start", "action", "settings.changed"]));
            await Error("unknown_action", () => controller.RunActionAsync("unknown"));
        });
        await Test("Recorded developer events preserve order and additive data", async () =>
        {
            string path = Path.Combine(Area(), "events.json");
            File.WriteAllText(path, """{"schemaVersion":1,"events":[{"name":"start"},{"name":"echo","data":{"futureField":true}},{"name":"stop"}]}""");
            var addon = new FakeAddon(); var replay = await EventReplay.RunAsync(addon, EventReplay.Load(path));
            True(addon.Events.SequenceEqual(["start", "echo", "stop"]));
            Same(JsonValue.Create(true), replay[1]!["result"]!["futureField"]);
        });
    }

    private static async Task SessionChecks()
    {
        await Test("Session ownership, resource limits and failure reservations", async () =>
        {
            var provider = new FakeProvider(); var registry = new SessionRegistry(provider, 2, 1);
            var one = registry.CreateOwner(); var two = registry.CreateOwner();
            string first = await registry.OpenAsync(one, "first", null, default);
            await Error("session_not_found", () => registry.StatusAsync(two, first, default));
            await Error("capacity_exceeded", () => registry.OpenAsync(one, "second", null, default));
            string second = await registry.OpenAsync(two, "second", null, default);
            await registry.ReleaseOwnerAsync(one); True(provider.Items["first"].Disposed);
            Same(JsonValue.Create("second"), (await registry.StatusAsync(two, second, default))["source"]);
            await registry.ReleaseOwnerAsync(two);
            var third = registry.CreateOwner();
            await Error("provider_failure", () => registry.OpenAsync(third, "fail", null, default));
            _ = await registry.OpenAsync(third, "third", null, default); await registry.ReleaseOwnerAsync(third);
        });
        await Test("Slow session does not block unrelated sessions", async () =>
        {
            var provider = new FakeProvider(); var registry = new SessionRegistry(provider);
            var one = registry.CreateOwner(); var two = registry.CreateOwner();
            var opening = registry.OpenAsync(one, "slow", null, default);
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            string second = await registry.OpenAsync(two, "fast", null, default).WaitAsync(TimeSpan.FromSeconds(1));
            _ = await registry.StatusAsync(two, second, default).WaitAsync(TimeSpan.FromSeconds(1));
            await registry.ReleaseOwnerAsync(one).WaitAsync(TimeSpan.FromSeconds(1));
            try { await opening; throw new Exception("Expected canceled open"); } catch (OperationCanceledException) { }
            await registry.ReleaseOwnerAsync(two);
        });
        await Test("Failed cleanup of a cancelled open retains capacity until released", async () =>
        {
            var provider = new LateProvider(); var registry = new SessionRegistry(provider, 1, 1);
            var one = registry.CreateOwner(); var two = registry.CreateOwner();
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Error("cleanup_failed", () => registry.OpenAsync(one, "late", null, cancelled.Token));
            await Error("capacity_exceeded", () => registry.OpenAsync(two, "another", null, default));
            provider.Session.FailClose = false;
            await registry.ReleaseOwnerAsync(one);
            _ = await registry.OpenAsync(two, "another", null, default);
            await registry.ReleaseOwnerAsync(two);
        });
        await Test("Latest frame queue copies input and drops old samples", async () =>
        {
            var queue = new LatestFrameQueue(); byte[] pixel = [1, 2, 3, 255];
            for (int i = 0; i < 10000; i++) True(queue.Publish("fixture", i, 1, 1, pixel));
            pixel[0] = 99;
            var frame = await queue.ReadAsync(default); True(frame.PresentationMicroseconds == 9999 && frame.Rgba8.Span[0] == 1);
            await Error("invalid_frame", () => queue.Publish("fixture", 0, 321, 1, pixel));
            queue.Complete(); True(!queue.Publish("fixture", 1, 1, 1, pixel));
        });
    }

    private static async Task RuntimeChecks(string runtime, string compiler, string dotnet)
    {
        var command = new WorkerCommand(Path.GetFullPath(dotnet), [typeof(AddonWorker).Assembly.Location]);
        AddonPackage? fixture = null;
        string directory = Area();
        await Test("Actual JavaScript compilation into a portable package", async () =>
        {
            string source = Path.Combine(directory, "source"); DeveloperTools.New(source, "org.example.runtime");
            File.WriteAllBytes(Path.Combine(source, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(SettingsManifest() with
                { Id = "org.example.runtime", Permissions = ["log.write", "storage.read", "storage.write", "sessions.manage"] }, Contract.Json));
            File.WriteAllText(Path.Combine(source, "addon.js"), """
                function onEvent(event, ajn) {
                    if (event.name === "start") { const n = (ajn.storage.get("starts") || 0) + 1; ajn.storage.set("starts", n); return n; }
                    if (event.name === "echo") return event.data;
                    if (event.name === "settings") return ajn.settings.get();
                    if (event.name === "action") return {settings:ajn.settings.get(),starts:ajn.storage.get("starts")};
                    if (event.name === "open") return ajn.sessions.open("fixture-session");
                    if (event.name === "badwire") { Javy.IO.writeSync(1, new TextEncoder().encode("{}\n")); return null; }
                    if (event.name === "oversize") { Javy.IO.writeSync(1, new TextEncoder().encode("x".repeat(140000) + "\n")); return null; }
                    if (event.name === "permission") { try { ajn.storage.set("blocked", true); } catch(e) { return e.code; } throw Error("unexpected grant"); }
                    if (event.name === "file") { try { Javy.IO.readSync(3, new Uint8Array(1)); } catch(e) { return "denied"; } throw Error("unexpected preopen"); }
                    if (event.name === "hang") { while (true) {} }
                    if (event.name === "idlehang") { Javy.IO.writeSync(1, new TextEncoder().encode(JSON.stringify({jsonrpc:"2.0",id:"host:"+event.eventId,result:true})+"\n")); while(true) {} }
                    if (event.name === "fault") throw Error("fixture fault");
                    if (event.name === "flood") { const b = new TextEncoder().encode("x".repeat(4096)); for(let i=0;i<100;i++) Javy.IO.writeSync(2,b); }
                    return null;
                }
                """);
            fixture = await DeveloperTools.BuildAsync(source, compiler, Path.Combine(directory, "fixture.ajnaddon"));
            True(fixture.Manifest.Id == "org.example.runtime");
        });
        if (fixture is null) return;
        var permissions = new PermissionGrant(fixture, ["storage.read", "storage.write"]);
        Task<AddonWorker> Start(PermissionGrant? grant = null) => AddonWorker.StartAsync(fixture, grant ?? permissions, runtime,
            Path.Combine(directory, "workers"), directory, command);
        await Test("Actual sandbox handshake, storage RPC and reload", async () =>
        {
            await using (var worker = await Start()) Same(JsonValue.Create(1), await worker.SendEventAsync("start"));
            await using (var worker = await Start()) Same(JsonValue.Create(2), await worker.SendEventAsync("start"));
            True(!Directory.EnumerateDirectories(Path.Combine(directory, "workers")).Any());
        });
        await Test("Actual sandbox denies storage grant and offers no file preopen", async () =>
        {
            await using var worker = await Start(new(fixture, []));
            Same(JsonValue.Create("permission_denied"), await worker.SendEventAsync("permission"));
            Same(JsonValue.Create("denied"), await worker.SendEventAsync("file"));
        });
        await Test("Hung addon is terminated while another remains responsive", async () =>
        {
            await using var hung = await Start(); await using var healthy = await Start();
            var waiting = Error("event_timeout", () => hung.SendEventAsync("hang"));
            Same(JsonValue.Create("healthy"), await healthy.SendEventAsync("echo", JsonValue.Create("healthy")));
            await waiting.WaitAsync(TimeSpan.FromSeconds(5)); True(hung.IsStopped);
            Same(JsonValue.Create(42), await healthy.SendEventAsync("echo", JsonValue.Create(42)));
        });
        await Test("Addon callback error releases worker and permits recovery", async () =>
        {
            await using var worker = await Start();
            await Error("addon_fault", () => worker.SendEventAsync("fault")); True(worker.IsStopped);
            await worker.DisposeAsync(); await worker.DisposeAsync();
            await using var restarted = await Start(); Same(JsonValue.Create(1), await restarted.SendEventAsync("echo", JsonValue.Create(1)));
        });
        await Test("Diagnostic flood is capped and stops the addon", async () =>
        {
            await using var worker = await Start();
            try { await worker.SendEventAsync("flood"); } catch (AddonException) { }
            for (int i = 0; i < 100 && !worker.IsStopped; i++) await Task.Delay(20);
            True(worker.IsStopped && worker.Diagnostics.Length <= 8192);
        });
        await Test("Actual worker settings match host-validated user values", async () =>
        {
            new AddonSettings(directory, fixture.Manifest).Update(new() { ["rate"] = 50, ["name"] = "saved" });
            await using var worker = await Start(new(fixture, []));
            var settings = await worker.SendEventAsync("settings"); Same(JsonValue.Create(50), settings!["rate"]);
            Same(JsonValue.Create("saved"), settings["name"]);
        });
        await Test("Worker validates wire data independently of SDK", async () =>
        {
            await using (var malformed = await Start()) await Error("invalid_message", () => malformed.SendEventAsync("badwire"));
            await using (var oversized = await Start()) await Error("message_too_large", () => oversized.SendEventAsync("oversize"));
        });
        await Test("Worker failure automatically releases its processing sessions", async () =>
        {
            var provider = new FakeProvider(); var sessions = new SessionRegistry(provider);
            await using var worker = await AddonWorker.StartAsync(fixture, new(fixture, ["sessions.manage"]), runtime,
                Path.Combine(directory, "workers"), directory, command, sessions: sessions);
            _ = await worker.SendEventAsync("open");
            await Error("addon_fault", () => worker.SendEventAsync("fault"));
            for (int i = 0; i < 100 && !provider.Items["fixture-session"].Disposed; i++) await Task.Delay(20);
            True(provider.Items["fixture-session"].Disposed);
        });
        await Test("Idle heartbeat terminates a guest that stops reading after its last event", async () =>
        {
            await using var worker = await Start();
            Same(JsonValue.Create(true), await worker.SendEventAsync("idlehang"));
            for (int i = 0; i < 450 && !worker.IsStopped; i++) await Task.Delay(20);
            True(worker.IsStopped, "Idle worker was not stopped by its heartbeat");
        });
        await Test("Actual worker retries failed processing cleanup without losing resource ownership", async () =>
        {
            var provider = new LateProvider(); var sessions = new SessionRegistry(provider, 1, 1);
            var worker = await AddonWorker.StartAsync(fixture, new(fixture, ["sessions.manage"]), runtime,
                Path.Combine(directory, "workers"), directory, command, sessions: sessions);
            try
            {
                _ = await worker.SendEventAsync("open");
                try { await worker.DisposeAsync(); throw new Exception("Expected failed processing cleanup"); }
                catch (AggregateException) { }
                var owner = sessions.CreateOwner();
                await Error("capacity_exceeded", () => sessions.OpenAsync(owner, "selected", null, default));
                provider.Session.FailClose = false;
                await worker.DisposeAsync();
                _ = await sessions.OpenAsync(owner, "selected", null, default);
                await sessions.ReleaseOwnerAsync(owner);
            }
            finally { provider.Session.FailClose = false; await worker.DisposeAsync(); }
        });
        await Test("Invalid startup cleans up and does not consume worker capacity", async () =>
        {
            var empty = Package();
            for (int i = 0; i < 9; i++)
                await Error("worker_exited", async () =>
                {
                    await using var unexpected = await AddonWorker.StartAsync(empty, new(empty, []), runtime,
                        Path.Combine(directory, "workers"), directory, command);
                });
            await using var worker = await Start(); Same(JsonValue.Create(7), await worker.SendEventAsync("echo", JsonValue.Create(7)));
        });
        await Test("Control-message latency sample (no GPU performance claim)", async () =>
        {
            await using var worker = await Start(); List<double> milliseconds = [];
            for (int i = 0; i < 40; i++)
            {
                var clock = Stopwatch.StartNew(); Same(JsonValue.Create(i), await worker.SendEventAsync("echo", JsonValue.Create(i)));
                if (i >= 5) milliseconds.Add(clock.Elapsed.TotalMilliseconds);
            }
            milliseconds.Sort();
            Console.WriteLine($"Echo median {milliseconds[milliseconds.Count / 2]:F3} ms; p95 {milliseconds[(int)(milliseconds.Count * .95)]:F3} ms");
            await File.WriteAllTextAsync(Path.Combine(root, "control-latency.json"), JsonSerializer.Serialize(new { milliseconds, note = "35 warmed stdio echo round trips, no video or GPU workload" }));
        });
        await Test("Management service drives a real sandbox through settings, actions and reconnect", async () =>
        {
            string managed = Area();
            new AddonRegistry(managed).Install(fixture, ["storage.read", "storage.write"]);
            var service = new AddonService(managed, async (p, g, log, token) => await AddonWorker.StartAsync(p, g, runtime,
                Path.Combine(managed, "workers"), managed, command, log: log, cancellationToken: token));
            using var stop = new CancellationTokenSource(); var server = new ManagementServer(managed, service).RunAsync(stop.Token, exitWhenIdle: false);
            try
            {
                await using var client = await ManagementClient.ConnectAsync(managed);
                True(service.HasRunningWorkers);
                await client.CallAsync("addons.configure", new() { ["id"] = fixture.Manifest.Id, ["changes"] = new JsonObject { ["name"] = "日本語 saved" } });
                var first = await client.CallAsync("addons.action", new() { ["id"] = fixture.Manifest.Id, ["action"] = "check" });
                Same(JsonValue.Create(1), first!["starts"]);
                Same(JsonValue.Create("日本語 saved"), first["settings"]!["name"]);
                await client.CallAsync("addons.start", new() { ["id"] = fixture.Manifest.Id });
                await client.DisposeAsync();
                await using var reconnect = await ManagementClient.ConnectAsync(managed);
                var second = await reconnect.CallAsync("addons.action", new() { ["id"] = fixture.Manifest.Id, ["action"] = "check" });
                Same(first, second);
                await reconnect.CallAsync("addons.stop", new() { ["id"] = fixture.Manifest.Id });
                True(!service.HasRunningWorkers);
                await reconnect.CallAsync("addons.start", new() { ["id"] = fixture.Manifest.Id });
                var restarted = await reconnect.CallAsync("addons.action", new() { ["id"] = fixture.Manifest.Id, ["action"] = "check" });
                Same(JsonValue.Create(2), restarted!["starts"]);
                await reconnect.CallAsync("addons.remove", new() { ["id"] = fixture.Manifest.Id });
                True(!service.HasRunningWorkers && (await reconnect.ListAsync()).Count == 0);
                Same(JsonValue.Create(2), new AddonStorage(managed, fixture.Manifest.Id).Get("starts"));
            }
            finally { stop.Cancel(); await server.WaitAsync(TimeSpan.FromSeconds(15)); }
        });
    }

    private static async Task ManagementChecks()
    {
        await Test("Management connections share activation and disconnect releases only their source", async () =>
        {
            string directory = Area(); var package = AddonPackage.Create(SettingsManifest(), EmptyModule);
            new AddonRegistry(directory).Install(package, []);
            var workers = new List<FakeAddon>();
            var service = new AddonService(directory, (_, _, log, _) =>
            {
                var worker = new FakeAddon(); workers.Add(worker); log("Started test addon"); return Task.FromResult<IAddonInstance>(worker);
            });
            using var stop = new CancellationTokenSource(); var server = new ManagementServer(directory, service).RunAsync(stop.Token, exitWhenIdle: false);
            try
            {
                await using var first = await ManagementClient.ConnectAsync(directory);
                await using var second = await ManagementClient.ConnectAsync(directory);
                True(workers.Count == 1);
                await first.DisposeAsync();
                True((await second.ListAsync())[0]!["running"]!.GetValue<bool>());
                await second.CallAsync("addons.start", new() { ["id"] = package.Manifest.Id });
                await second.DisposeAsync();
                await Task.Delay(100); True(service.HasRunningWorkers, "Manual background activation should outlive Manager");
                await using var third = await ManagementClient.ConnectAsync(directory);
                await third.CallAsync("addons.stop", new() { ["id"] = package.Manifest.Id });
                True(!service.HasRunningWorkers);
                var settings = await third.CallAsync("addons.configure", new() { ["id"] = package.Manifest.Id, ["changes"] = new JsonObject { ["rate"] = 40 } });
                Same(JsonValue.Create(40), settings!["rate"]);
                True(((JsonArray)(await third.CallAsync("addons.logs", new() { ["id"] = package.Manifest.Id }))!).Count > 0);
                await Error("host_running", () => new HostLease(directory));
            }
            finally { stop.Cancel(); await server.WaitAsync(TimeSpan.FromSeconds(10)); }
        });
        await Test("Management package review is hash-bound and a corrupt addon remains removable", async () =>
        {
            string directory = Area(); var registry = new AddonRegistry(directory);
            var original = AddonPackage.Create(SettingsManifest(), EmptyModule);
            string file = Path.Combine(directory, "addon.ajnaddon"); original.Save(file);
            var broken = Package(id: "org.example.broken"); registry.Install(broken, []);
            File.WriteAllText(Path.Combine(directory, "installed", broken.Manifest.Id, "active.json"), "{broken");
            var service = new AddonService(directory, (_, _, _, _) => Task.FromResult<IAddonInstance>(new FakeAddon()));
            using var stop = new CancellationTokenSource(); var server = new ManagementServer(directory, service).RunAsync(stop.Token, exitWhenIdle: false);
            try
            {
                await using var client = await ManagementClient.ConnectAsync(directory);
                var inspected = await client.CallAsync("addons.inspect", new() { ["path"] = file });
                AddonPackage.Create(SettingsManifest("1.1.0"), EmptyModule).Save(file);
                try
                {
                    await client.CallAsync("addons.installDev", new() { ["path"] = file, ["expectedHash"] = inspected!["hash"]!.DeepClone(), ["permissions"] = new JsonArray() });
                    throw new Exception("Expected changed package rejection");
                }
                catch (ManagementException error) when (error.Code == "integrity_mismatch") { }
                original.Save(file);
                _ = await client.CallAsync("addons.installDev", new() { ["path"] = file, ["expectedHash"] = original.Hash, ["permissions"] = new JsonArray() });
                var list = await client.ListAsync();
                True(list.Count == 2 && list.Any(item => item!["id"]!.GetValue<string>() == broken.Manifest.Id && item["error"] is not null));
                await client.CallAsync("addons.remove", new() { ["id"] = broken.Manifest.Id });
                True((await client.ListAsync()).Count == 1);
            }
            finally { stop.Cancel(); await server.WaitAsync(TimeSpan.FromSeconds(10)); }
        });
        await Test("Management pipe restricts its owner and denies network logons", async () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            string directory = Area();
            var service = new AddonService(directory, (_, _, _, _) => Task.FromResult<IAddonInstance>(new FakeAddon()));
            using var stop = new CancellationTokenSource(); var server = new ManagementServer(directory, service).RunAsync(stop.Token, exitWhenIdle: false);
            try
            {
                using var pipe = new NamedPipeClientStream(".", ManagementClient.PipeName(directory), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(2000);
                var security = pipe.GetAccessControl();
                using var identity = WindowsIdentity.GetCurrent();
                True(security.GetOwner(typeof(SecurityIdentifier))!.Equals(identity.Owner));
                var network = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
                var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
                bool deniesNetwork = false;
                foreach (var rule in rules)
                {
                    if (rule.IdentityReference.Equals(network) && rule.AccessControlType == AccessControlType.Deny) deniesNetwork = true;
                    if (rule.AccessControlType == AccessControlType.Allow) True(rule.IdentityReference.Equals(identity.Owner));
                }
                True(deniesNetwork);
            }
            finally { stop.Cancel(); await server.WaitAsync(TimeSpan.FromSeconds(10)); }
        });
        await Test("Management paginates installed addons and bounds Unicode logs", async () =>
        {
            string directory = Area(); var registry = new AddonRegistry(directory);
            for (int i = 0; i < 33; i++) registry.Install(Package(id: "org.example.page" + i.ToString("D3")), []);
            var service = new AddonService(directory, (_, _, log, _) =>
            {
                for (int i = 0; i < 40; i++) log(new string('語', 1024));
                return Task.FromResult<IAddonInstance>(new FakeAddon());
            });
            using var stop = new CancellationTokenSource(); var server = new ManagementServer(directory, service).RunAsync(stop.Token, exitWhenIdle: false);
            try
            {
                await using var client = await ManagementClient.ConnectAsync(directory);
                var all = await client.ListAsync(); True(all.Count == 33);
                True(all.Select(n => n!["id"]!.GetValue<string>()).Distinct().Count() == 33);
                await client.CallAsync("addons.start", new() { ["id"] = "org.example.page000" });
                var logs = (JsonArray)(await client.CallAsync("addons.logs", new() { ["id"] = "org.example.page000" }))!;
                True(logs.Count == 32 && logs.All(l => l!.GetValue<string>().Length == 512));
                True((await client.ListAsync()).Count == 33, "Large log response broke the next operation");
            }
            finally { stop.Cancel(); await server.WaitAsync(TimeSpan.FromSeconds(10)); }
        });
        await Test("Failed lookups release host slots and unavailable rollback preserves running work", async () =>
        {
            string directory = Area();
            await using var service = new AddonService(directory, (_, _, _, _) => Task.FromResult<IAddonInstance>(new FakeAddon()));
            await service.InvokeAsync("test", "manager.hello", new() { ["major"] = 1L }, default);
            for (int i = 0; i < 140; i++)
                await Error("not_installed", () => service.InvokeAsync("test", "addons.settings", new() { ["id"] = "org.example.missing" + i }, default));
            var package = Package(); new AddonRegistry(directory).Install(package, []);
            await service.InvokeAsync("test", "addons.start", new() { ["id"] = package.Manifest.Id }, default);
            await Error("no_previous_version", () => service.InvokeAsync("test", "addons.rollback", new() { ["id"] = package.Manifest.Id }, default));
            True(service.HasRunningWorkers);
        });
    }

    private sealed class ChunkedStream(byte[] bytes, int chunk) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(chunk, buffer.Length)], cancellationToken);
    }
    private sealed class FakeAddon : IAddonInstance
    {
        public bool IsStopped { get; private set; }
        public readonly List<string> Events = [];
        public Task<JsonNode?> SendEventAsync(string name, JsonNode? data = null, CancellationToken cancellationToken = default)
        {
            Events.Add(name); return Task.FromResult(data?.DeepClone());
        }
        public ValueTask DisposeAsync() { IsStopped = true; return ValueTask.CompletedTask; }
    }
    private sealed class FakeProvider : IProcessingSessionProvider
    {
        public readonly Dictionary<string, FakeSession> Items = [];
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IProcessingSession> OpenAsync(string sourceId, string? profileId, CancellationToken cancellationToken)
        {
            if (sourceId == "fail") throw new AddonException("provider_failure", "Fixture error");
            if (sourceId == "slow") { Entered.SetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
            var item = new FakeSession(sourceId); Items.Add(sourceId, item); return item;
        }
    }
    private sealed class FakeSession(string source) : IProcessingSession
    {
        public bool Disposed;
        public Task<JsonObject> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(new JsonObject { ["source"] = source });
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class LateProvider : IProcessingSessionProvider
    {
        public readonly FailingCloseSession Session = new();
        public Task<IProcessingSession> OpenAsync(string sourceId, string? profileId, CancellationToken cancellationToken) => Task.FromResult<IProcessingSession>(Session);
    }
    private sealed class FailingCloseSession : IProcessingSession
    {
        public bool FailClose = true;
        public Task<JsonObject> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(new JsonObject());
        public ValueTask DisposeAsync()
        {
            if (FailClose) throw new AddonException("cleanup_failed", "Fixture could not release resource");
            return ValueTask.CompletedTask;
        }
    }
}
