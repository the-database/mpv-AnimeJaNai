using System.Diagnostics;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Native;

internal static class NativeAddonChecks
{
    public static async Task RunAsync(string root, string output, WorkerCommand command, string runtime, string compiler, List<JsonObject> evidence)
    {
        string area = Path.Combine(output, "real-addon"), sourceDirectory = Path.Combine(area, "source");
        Directory.CreateDirectory(area);
        DeveloperTools.New(sourceDirectory, "org.animejanai.media-test");
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(sourceDirectory, "manifest.json")))!;
        manifest["api"]!["minMinor"] = 1;
        manifest["requiredCapabilities"] = new JsonObject { ["sessions"] = new JsonObject { ["major"] = 1, ["minMinor"] = 1 } };
        manifest["permissions"] = new JsonArray("sessions.manage");
        File.WriteAllText(Path.Combine(sourceDirectory, "manifest.json"), manifest.ToJsonString());
        File.WriteAllText(Path.Combine(sourceDirectory, "addon.js"), """
            let ids = [];
            function onEvent(event, ajn) {
                switch (event.name) {
                    case "selections": return ajn.sessions.selections();
                    case "unapproved":
                        try { ajn.sessions.open("not-approved", "not-approved"); return "unexpected"; }
                        catch (error) { return error.code; }
                    case "open": {
                        const s = ajn.sessions.selections();
                        ids.push(ajn.sessions.open(s.sources[0].id, s.profiles[0].id).sessionId);
                        ids.push(ajn.sessions.open(s.sources[0].id, s.profiles[0].id).sessionId);
                        try { ajn.sessions.open(s.sources[0].id, s.profiles[0].id); return "unexpected"; }
                        catch (error) { return { ids, limit: error.code }; }
                    }
                    case "status": return ids.map(id => ajn.sessions.status(id));
                    case "pause": ajn.sessions.pause(ids[0], true); return null;
                    case "seek": ajn.sessions.seek(ids[0], 2.5); return null;
                    case "closeFirst": ajn.sessions.requestClose(ids.shift()); return null;
                    case "finish": ajn.sessions.seek(ids[0], 89.7); return null;
                }
            }
            """);
        var package = await DeveloperTools.BuildAsync(sourceDirectory, compiler, Path.Combine(area, "media-test.ajnaddon"));
        var grant = new PermissionGrant(package, ["sessions.manage"]);
        var selections = new MediaSelections(area);
        var registry = new SessionRegistry(new NativeSessionProvider(root, area, selections, command), perOwnerLimit: 16);
        await using (var addon = await AddonWorker.StartAsync(package, grant, runtime, Path.Combine(area, "workers"), area, command, sessions: registry))
        {
            var denied = await addon.SendEventAsync("unapproved");
            if (denied?.GetValue<string>() != "source_not_granted") throw new Exception("An unapproved source was accepted.");
            var empty = (JsonObject)(await addon.SendEventAsync("selections"))!;
            if (((JsonArray)empty["sources"]!).Count != 0) throw new Exception("Media access was granted by default.");
            selections.ApproveSource(package, grant, Path.Combine(root, "animejanai", "benchmarks", "480x360.mp4"));
            selections.ApproveProfile(package, grant, "Saved balanced", 1002, "DirectML", "[global]\nconfig_version=3\nbackend=TensorRT\n");
            var opened = (JsonObject)(await addon.SendEventAsync("open"))!;
            if (opened["limit"]?.GetValue<string>() != "capacity_exceeded") throw new Exception("Native admission did not enforce its limit.");
            evidence.Add(new() { ["realAddonOpened"] = opened });
            async Task<JsonArray> Until(Func<JsonArray, bool> ready)
            {
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed < TimeSpan.FromSeconds(25))
                {
                    var status = (JsonArray)(await addon.SendEventAsync("status"))!;
                    if (status.Any(s => s?["state"]?.GetValue<string>() == "failed")) throw new Exception("Native addon session failed: " + status);
                    if (ready(status)) { evidence.Add(new() { ["realAddonStatus"] = status.DeepClone() }); return status; }
                    await Task.Delay(60);
                }
                throw new TimeoutException("Real addon did not reach the expected native state.");
            }
            await Until(s => s.All(v => v?["state"]?.GetValue<string>() == "running" && v["outputWidth"]?.GetValue<long>() == 960));
            await addon.SendEventAsync("pause");
            var paused = await Until(s => s[0]?["paused"]?.GetValue<bool>() == true);
            double previous = paused[1]!["positionSeconds"]!.GetValue<double>();
            await Until(s => s[1]?["positionSeconds"]?.GetValue<double>() > previous + .3);
            await addon.SendEventAsync("seek");
            await Until(s => Math.Abs((s[0]?["positionSeconds"]?.GetValue<double>() ?? -99) - 2.5) < .15);
            await addon.SendEventAsync("closeFirst");
            await addon.SendEventAsync("finish");
            await Until(s => s[0]?["state"]?.GetValue<string>() == "completed");
        }
        if (Directory.EnumerateDirectories(Path.Combine(area, "media-workers")).Any()) throw new Exception("Stopping the addon left native session files behind.");
        Console.WriteLine("PASS actual Wasm addon: default-denied sources, profile snapshot, two 2x sessions, admission limit, independent controls, EOF and cleanup.");
    }
}
