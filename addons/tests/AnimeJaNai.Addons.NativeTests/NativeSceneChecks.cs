using System.Text;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Management;

internal static class NativeSceneChecks
{
    internal static async Task RunAsync(string root, string output, string compiler, List<JsonObject> evidence, bool nativeMvtools = false)
    {
        string data = Path.Combine(output, "data"), addonData = Path.Combine(data, "addons");
        string source = await NativeFrameChecks.CreatePatternAsync(root, output);
        string config = Path.Combine(output, "animejanai.conf");
        string backend = Environment.GetEnvironmentVariable("AJN_SCENE_BACKEND") ?? "DirectML";
        if (backend is not ("DirectML" or "TensorRT")) throw new ArgumentException("Choose DirectML or TensorRT.");
        File.WriteAllText(config, $"[global]\nconfig_version=3\nbackend={backend}\nlogging=yes\ndefault_slot=1\n" +
            "[slot_1]\nprofile_name=Scene hook check\nchain_1_max_fps=61\nchain_1_rife=yes\nchain_1_rife_model=414\n" +
            "chain_1_rife_factor_numerator=2\nchain_1_rife_factor_denominator=1\nchain_1_rife_scene_detect_threshold=0.000001\n");
        foreach (int slot in new[] { 2, 3, 4 })
            File.AppendAllText(config, $"[slot_{slot}]\nchain_1_max_fps=61\nchain_1_rife=yes\nchain_1_rife_model=414\n" +
                "chain_1_rife_factor_numerator=2\nchain_1_rife_factor_denominator=1\n" +
                "chain_1_model_1_name=2x_AnimeJaNai_HD_V3.1_Performance_SPANF3_b5f48_unshuffle_fp16\n" +
                $"chain_1_rife_before_upscale={(slot == 2 ? "no" : "yes")}\n" +
                $"chain_1_model_1_resize_factor_before_upscale={(slot == 4 ? 50 : 100)}\n");
        var package = await DeveloperTools.BuildAsync(Path.Combine(AppContext.BaseDirectory, nativeMvtools ? "rife-settings" : "scene-detector"), compiler, Path.Combine(output, "scene.ajnaddon"));
        new AddonRegistry(addonData).Install(package, package.Manifest.Permissions);
        string Q(string s) => "%" + Encoding.UTF8.GetByteCount(s) + "%" + s;
        string filter = "--vf=@aji:animejanai:lib=" + Q(Path.Combine(root, "animejanai/inference/aji.dll")) +
            ":conf=" + Q(config) + ":model-dir=" + Q(Path.Combine(root, "animejanai/onnx")) +
            ":rife-model-dir=" + Q(Path.Combine(root, "animejanai/rife")) +
            ":trtexec=" + Q(Path.Combine(root, "animejanai/inference/trtexec.exe")) + ":slot=1";
        await using var player = new NativeLifecycleChecks.TestPlayer(root, data, output, "scene", "mpv.exe",
            [backend == "DirectML" ? "--hwdec=d3d11va" : "--hwdec=nvdec", "--vo-null-accept-hwframes=yes", "--loop-file=inf", filter], source);
        await player.ConnectAsync();
        ManagementClient? manager = null;
        async Task Until(Func<Task<bool>> ready)
        {
            // A first TensorRT run builds engines for each new model/resolution.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(backend == "TensorRT" ? 300 : 40));
            while (!await ready()) await Task.Delay(100, timeout.Token);
        }
        await Until(async () => {
            try { manager = await ManagementClient.ConnectAsync(addonData); return true; }
            catch (Exception e) when (e is IOException or TimeoutException) { return false; }
        });
        Task<JsonNode?> Call(string method, JsonObject? request = null)
        { request ??= new(); request["id"] = package.Manifest.Id; return manager!.CallAsync(method, request); }
        Task<JsonNode?> Action(string id) => Call("addons.action", new() { ["action"] = id });
        JsonNode Pair(JsonObject state) => nativeMvtools ? state : state["lastPair"]!;
        bool LibraryLoaded()
        {
            using var process = System.Diagnostics.Process.GetProcessById(player.Id);
            return process.Modules.Cast<System.Diagnostics.ProcessModule>().Any(m => m.ModuleName.Equals("ajn-mvtools.dll", StringComparison.OrdinalIgnoreCase));
        }
        async Task<JsonObject> State(Func<JsonObject, bool> accept)
        {
            JsonObject? latest = null;
            await Until(async () => {
                var response = await Action("status");
                latest = (nativeMvtools ? response?["players"]?.AsArray() : response?.AsArray())?.FirstOrDefault() as JsonObject;
                if (latest?["state"]?.GetValue<string>() is "suspended" or "backendUnavailable" or "sampleUnavailable" or "detectorUnavailable" or "detectorError")
                    throw new Exception("Native scene detector cannot run: " + latest);
                return latest is not null && accept(latest);
            });
            return latest!;
        }
        try
        {
            JsonObject continuous;
            if (nativeMvtools)
            {
                // on_player must activate it; do not explicitly start/attach it.
                continuous = await State(s => s["acceptedPairs"]?.GetValue<int>() >= 20);
                if (!LibraryLoaded()) throw new Exception("MVTools was not loaded during native analysis.");
                evidence.Add(new() { ["automaticNativeAttachment"] = continuous.DeepClone(), ["backend"] = backend });
                await Call("addons.configure", new() { ["changes"] = new JsonObject { ["thscd1"] = 0, ["thscd2"] = 0 } });
                var cuts = await State(s => s["cutPairs"]?.GetValue<int>() >= 10);
                evidence.Add(new() { ["nativeThresholdsApplied"] = cuts.DeepClone() });
                await Call("addons.configure", new() { ["changes"] = new JsonObject { ["thscd1"] = 240, ["thscd2"] = 130 } });
                continuous = await State(s => s["acceptedPairs"]?.GetValue<int>() >= 10);
            }
            else
            {
            await Call("addons.configure", new() { ["changes"] = new JsonObject { ["decision"] = "cut" } });
            await Call("addons.start");
            await Until(async () => (await Action("attach"))?["attached"]?.GetValue<int>() == 1);
            var cut = await State(s => s["acceptedPairs"]!.GetValue<int>() >= 20);
            if (cut["lastPair"]?["decision"]?.GetValue<string>() != "cut") throw new Exception("Wrong cut decision");
            evidence.Add(new() { ["forcedCut"] = cut.DeepClone() });
            int before = cut["acceptedPairs"]!.GetValue<int>();
            await Call("addons.configure", new() { ["changes"] = new JsonObject { ["decision"] = "continuous" } });
            continuous = await State(s => s["acceptedPairs"]!.GetValue<int>() >= before + 20 && s["lastPair"]?["decision"]?.GetValue<string>() == "continuous");
            evidence.Add(new() { ["forcedContinuous"] = continuous.DeepClone() });
            }
            string epoch = Pair(continuous)["epoch"]!.GetValue<string>();
            await player.CommandAsync("seek", "10", "absolute+exact");
            var seek = await State(s => Pair(s)["epoch"]?.GetValue<string>() != epoch &&
                Pair(s)["currentPtsSeconds"]?.GetValue<double>() >= 10);
            evidence.Add(new() { ["seek"] = seek.DeepClone() });
            if (nativeMvtools)
            {
                await Call("addons.configure", new() { ["changes"] = new JsonObject { ["sceneMode"] = "default" } });
                await Until(() => Task.FromResult(!LibraryLoaded()));
                evidence.Add(new() { ["defaultModeUnloadsNativeLibrary"] = true });
            }
            else
            {
                await Action("detach");
                if (((JsonArray)(await Action("status"))!).Count != 0) throw new Exception("Detector remained attached");
            }
            double position = await player.PositionAsync();
            await Until(async () => await player.PositionAsync() > position + .2);
            if (nativeMvtools) await Call("addons.configure", new() { ["changes"] = new JsonObject { ["sceneMode"] = "mvtools" } });
            else await Action("attach");
            var reattached = await State(s => s["acceptedPairs"]!.GetValue<int>() >= 5);
            evidence.Add(new() { ["detachPlaybackContinued"] = true, ["reattached"] = reattached.DeepClone(), ["backend"] = backend });
            // Slot 4 also covers cold TensorRT builds: the upscale engine may
            // become ready before RIFE, after hoisting its 50% input resize.
            foreach (int slot in new[] { 2, 3, 4 })
            {
                int count = reattached["acceptedPairs"]!.GetValue<int>();
                await player.CommandAsync("vf-command", "aji", "slot", slot.ToString());
                reattached = await State(s => s["acceptedPairs"]!.GetValue<int>() >= count + 8 &&
                    Pair(s)["sourceWidth"]?.GetValue<int>() == (slot == 2 ? 960 : slot == 4 ? 240 : 480));
                evidence.Add(new() { ["rifeBeforeUpscale"] = slot != 2,
                    ["resizeBeforeUpscalePercent"] = slot == 4 ? 50 : 100, ["status"] = reattached.DeepClone() });
            }
            await Call("addons.stop");
            if (nativeMvtools) await Until(() => Task.FromResult(!LibraryLoaded()));
            await player.QuitAsync();
            string log = File.ReadAllText(Path.Combine(output, "scene-mpv.log"));
            if (log.Contains("interpolation failed:", StringComparison.Ordinal) ||
                log.Contains("inference failed:", StringComparison.Ordinal) ||
                log.Contains("Disabling filter aji", StringComparison.Ordinal))
                throw new Exception("RIFE/upscale playback failed; see scene-mpv.log");
        }
        finally
        {
            if (manager is not null) { try { await Call("addons.stop"); } finally { await manager.DisposeAsync(); } }
        }
    }
}
