using System.Text;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Management;

internal static class NativeSceneChecks
{
    internal static async Task RunAsync(string root, string output, string compiler, List<JsonObject> evidence)
    {
        string data = Path.Combine(output, "data"), addonData = Path.Combine(data, "addons");
        string source = await NativeFrameChecks.CreatePatternAsync(root, output);
        string config = Path.Combine(output, "animejanai.conf");
        File.WriteAllText(config, "[global]\nconfig_version=3\nbackend=DirectML\nlogging=yes\ndefault_slot=1\n" +
            "[slot_1]\nprofile_name=Scene hook check\nchain_1_max_fps=61\nchain_1_rife=yes\nchain_1_rife_model=414\n" +
            "chain_1_rife_factor_numerator=2\nchain_1_rife_factor_denominator=1\nchain_1_rife_scene_detect_threshold=0.000001\n");
        foreach (int slot in new[] { 2, 3 })
            File.AppendAllText(config, $"[slot_{slot}]\nchain_1_max_fps=61\nchain_1_rife=yes\nchain_1_rife_model=414\n" +
                "chain_1_rife_factor_numerator=2\nchain_1_rife_factor_denominator=1\n" +
                "chain_1_model_1_name=2x_AnimeJaNai_HD_V3.1_Performance_SPANF3_b5f48_unshuffle_fp16\n" +
                $"chain_1_rife_before_upscale={(slot == 2 ? "no" : "yes")}\n");
        var package = await DeveloperTools.BuildAsync(Path.Combine(AppContext.BaseDirectory, "scene-detector"), compiler, Path.Combine(output, "scene.ajnaddon"));
        new AddonRegistry(addonData).Install(package, package.Manifest.Permissions);
        string Q(string s) => "%" + Encoding.UTF8.GetByteCount(s) + "%" + s;
        string filter = "--vf=@aji:animejanai:lib=" + Q(Path.Combine(root, "animejanai/inference/aji.dll")) +
            ":conf=" + Q(config) + ":model-dir=" + Q(Path.Combine(root, "animejanai/onnx")) +
            ":rife-model-dir=" + Q(Path.Combine(root, "animejanai/rife")) + ":slot=1";
        await using var player = new NativeLifecycleChecks.TestPlayer(root, data, output, "scene", "mpv.exe",
            ["--hwdec=d3d11va", "--vo-null-accept-hwframes=yes", "--loop-file=inf", filter], source);
        await player.ConnectAsync();
        ManagementClient? manager = null;
        async Task Until(Func<Task<bool>> ready)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            while (!await ready()) await Task.Delay(100, timeout.Token);
        }
        await Until(async () => {
            try { manager = await ManagementClient.ConnectAsync(addonData); return true; }
            catch (Exception e) when (e is IOException or TimeoutException) { return false; }
        });
        Task<JsonNode?> Call(string method, JsonObject? request = null)
        { request ??= new(); request["id"] = package.Manifest.Id; return manager!.CallAsync(method, request); }
        Task<JsonNode?> Action(string id) => Call("addons.action", new() { ["action"] = id });
        async Task<JsonObject> State(Func<JsonObject, bool> accept)
        {
            JsonObject? latest = null;
            await Until(async () => {
                latest = ((JsonArray)(await Action("status"))!).FirstOrDefault() as JsonObject;
                if (latest?["state"]?.GetValue<string>() is "suspended" or "backendUnavailable" or "sampleUnavailable")
                    throw new Exception("Native scene detector cannot run: " + latest);
                return latest is not null && accept(latest);
            });
            return latest!;
        }
        try
        {
            await Call("addons.configure", new() { ["changes"] = new JsonObject { ["decision"] = "cut" } });
            await Call("addons.start");
            await Until(async () => (await Action("attach"))?["attached"]?.GetValue<int>() == 1);
            var cut = await State(s => s["acceptedPairs"]!.GetValue<int>() >= 20);
            if (cut["lastPair"]?["decision"]?.GetValue<string>() != "cut") throw new Exception("Wrong cut decision");
            evidence.Add(new() { ["forcedCut"] = cut.DeepClone() });
            int before = cut["acceptedPairs"]!.GetValue<int>();
            await Call("addons.configure", new() { ["changes"] = new JsonObject { ["decision"] = "continuous" } });
            var continuous = await State(s => s["acceptedPairs"]!.GetValue<int>() >= before + 20 && s["lastPair"]?["decision"]?.GetValue<string>() == "continuous");
            evidence.Add(new() { ["forcedContinuous"] = continuous.DeepClone() });
            string epoch = continuous["lastPair"]!["epoch"]!.GetValue<string>();
            await player.CommandAsync("seek", "10", "absolute+exact");
            var seek = await State(s => s["lastPair"]?["epoch"]?.GetValue<string>() != epoch &&
                s["lastPair"]?["currentPtsSeconds"]?.GetValue<double>() >= 10);
            evidence.Add(new() { ["seek"] = seek.DeepClone() });
            await Action("detach");
            if (((JsonArray)(await Action("status"))!).Count != 0) throw new Exception("Detector remained attached");
            double position = await player.PositionAsync();
            await Until(async () => await player.PositionAsync() > position + .2);
            await Action("attach");
            var reattached = await State(s => s["acceptedPairs"]!.GetValue<int>() >= 5);
            evidence.Add(new() { ["detachPlaybackContinued"] = true, ["reattached"] = reattached.DeepClone(), ["backend"] = "DirectML", ["decisionBudgetMs"] = 25 });
            foreach (int slot in new[] { 2, 3 })
            {
                int count = reattached["acceptedPairs"]!.GetValue<int>();
                await player.CommandAsync("vf-command", "aji", "slot", slot.ToString());
                reattached = await State(s => s["acceptedPairs"]!.GetValue<int>() >= count + 8 &&
                    s["lastPair"]?["sourceWidth"]?.GetValue<int>() == (slot == 2 ? 960 : 480));
                evidence.Add(new() { ["rifeBeforeUpscale"] = slot == 3, ["status"] = reattached.DeepClone() });
            }
            await Call("addons.stop");
            await player.QuitAsync();
            if (File.ReadAllText(Path.Combine(output, "scene-mpv.log")).Contains("interpolation failed:", StringComparison.Ordinal))
                throw new Exception("RIFE interpolation failed; see scene-mpv.log");
        }
        finally
        {
            if (manager is not null) { try { await Call("addons.stop"); } finally { await manager.DisposeAsync(); } }
        }
    }
}
