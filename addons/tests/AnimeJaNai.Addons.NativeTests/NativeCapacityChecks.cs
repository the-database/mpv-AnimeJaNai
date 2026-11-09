using System.Diagnostics;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Native;

internal static class NativeCapacityChecks
{
    public static async Task RunAsync(string root, string output, WorkerCommand command, List<JsonObject> evidence)
    {
        string area = Path.Combine(output, "capacity"); Directory.CreateDirectory(area);
        var package = AddonPackage.Create(new() { SchemaVersion = 1, Id = "org.animejanai.capacity-test", Name = "Capacity test", Version = "0.1.0",
            Api = new() { Major = 1, MinMinor = 1 }, ModuleSha256 = new string('0', 64), Permissions = ["sessions.manage"] }, [0, 97, 115, 109, 1, 0, 0, 0]);
        var grant = new PermissionGrant(package, package.Manifest.Permissions);
        var media = new MediaSelections(area);
        string source = media.ApproveSource(package, grant, Path.Combine(root, "animejanai", "benchmarks", "480x360.mp4"));
        string profile = media.ApproveProfile(package, grant, "Test DirectML", 1002, "DirectML", "[global]\nconfig_version=3\nbackend=DirectML\n");
        var settings = new HostSettings(area); settings.Update(1);
        var registry = new SessionRegistry(new NativeSessionProvider(root, area, media, command, hostSettings: settings));
        await using var broker = new Broker(package, grant, area, sessions: registry);
        await using var manager = new AddonService(area, (_, _, _, _) => throw new NotSupportedException(), hostSettings: settings);
        await manager.InvokeAsync("test-manager", "manager.hello", new() { ["major"] = 1L }, default);
        Task<JsonNode?> Limit(long count) => manager.InvokeAsync("test-manager", "host.configure", new() { ["maximumConcurrentSessions"] = count }, default);
        async Task<string> Open()
        {
            var result = await broker.InvokeAsync("sessions.open", new() { ["sourceId"] = source, ["profileId"] = profile }, default);
            return result!["sessionId"]!.GetValue<string>();
        }
        async Task Denied()
        {
            try { await Open(); throw new Exception("Native capacity was not enforced."); }
            catch (AddonException error) when (error.Code == "capacity_exceeded") { }
        }
        async Task<JsonObject> Until(string id, Func<JsonObject, bool> ready)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(25))
            {
                var state = (JsonObject)(await broker.InvokeAsync("sessions.status", new() { ["sessionId"] = id }, default))!;
                Require(state["state"]?.GetValue<string>() != "failed", "Native session failed: " + state);
                if (ready(state)) return state;
                await Task.Delay(40);
            }
            throw new TimeoutException("Native capacity fixture did not advance.");
        }
        async Task Close(string id) => _ = await broker.InvokeAsync("sessions.close", new() { ["sessionId"] = id }, default);
        string first = await Open(); await Until(first, s => s["outputWidth"]?.GetValue<long>() == 960); await Denied();
        await Limit(2); string second = await Open();
        var secondReady = await Until(second, s => s["outputWidth"]?.GetValue<long>() == 960);
        double before = secondReady["positionSeconds"]!.GetValue<double>();
        await Limit(1); await Denied();
        var progressed = await Until(second, s => s["positionSeconds"]?.GetValue<double>() > before + .3);
        Require((await broker.InvokeAsync("sessions.selections", new(), default))!["maximumConcurrentSessions"]!.GetValue<int>() == 1, "Guest discovery has stale capacity.");
        await Close(first); await Denied();
        await Close(second); string third = await Open(); await Until(third, s => s["outputWidth"]?.GetValue<long>() == 960); await Close(third);
        Require(new HostSettings(area).MaximumConcurrentSessions == 1, "Saved capacity changed on restart.");
        Require(!Directory.EnumerateDirectories(Path.Combine(area, "media-workers")).Any(), "Capacity changes lost owned cleanup.");
        evidence.Add(new() { ["dynamicCapacity"] = true, ["raisedFrom"] = 1, ["raisedTo"] = 2, ["loweredTo"] = 1,
            ["existingSessionContinued"] = progressed, ["newAdmissionResumedAfterClose"] = true, ["savedLimit"] = 1 });
        Console.WriteLine("PASS native live capacity: raise/lower, active sessions preserved, discovery, admission after close, persistence and cleanup.");
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
