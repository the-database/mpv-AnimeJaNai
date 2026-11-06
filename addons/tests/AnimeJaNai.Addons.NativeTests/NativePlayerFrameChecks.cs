using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Management;

internal static class NativePlayerFrameChecks
{
    internal static async Task RunAsync(string root, string output, string compiler, List<JsonObject> evidence)
    {
        string data = Path.Combine(output, "data"), addons = Path.Combine(data, "addons");
        string source = await NativeFrameChecks.CreatePatternAsync(root, output);
        string config = Path.Combine(output, "animejanai.conf");
        File.WriteAllText(config, "[global]\nconfig_version=3\nbackend=DirectML\nlogging=no\ndefault_slot=1002\n");
        var original = await DeveloperTools.BuildAsync(Path.Combine(AppContext.BaseDirectory, "player-inspector"), compiler, Path.Combine(output, "player-inspector.ajnaddon"));
        string module = Path.Combine(output, "inspector.wasm"); original.WriteModule(module);
        var packages = Enumerable.Range(1, 3).Select(i => AddonPackage.Create(original.Manifest with { Id = "org.animejanai.playercheck" + i }, File.ReadAllBytes(module))).ToArray();
        var registry = new AddonRegistry(addons);
        foreach (var package in packages) registry.Install(package, package.Manifest.Permissions);
        string[] Settings(string name, bool render)
        {
            string Quote(string value) => "%" + Encoding.UTF8.GetByteCount(value) + "%" + value;
            var arguments = new Dictionary<string, string> {
                ["lib"] = Path.Combine(root, "animejanai/inference/aji.dll"), ["conf"] = config,
                ["model-dir"] = Path.Combine(root, "animejanai/onnx"), ["rife-model-dir"] = Path.Combine(root, "animejanai/rife"),
                ["trtexec"] = Path.Combine(root, "animejanai/inference/trtexec.exe"), ["stats"] = Path.Combine(output, name + "-inference.log"),
            };
            return ["--hwdec=d3d11va", "--loop-file=inf", "--vf=@aji:animejanai:" + string.Join(':', arguments.Select(p => p.Key + "=" + Quote(p.Value))) + ":slot=1002",
                .. render ? new[] { "--vo=gpu-next", "--gpu-api=d3d11", "--gpu-context=d3d11", "--window-minimized=yes", "--force-window=yes" } : []];
        }
        static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        async Task Until(Func<Task<bool>> ready, int seconds = 30)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            while (!await ready()) await Task.Delay(60, timeout.Token);
        }
        var expectedSettings = new[] { "input.conf", "settings.xml" }.ToDictionary(n => n, n => SHA256.HashData(File.ReadAllBytes(Path.Combine(root, "portable_config", n))));
        await using var first = new NativeLifecycleChecks.TestPlayer(root, data, output, "first", "mpv.exe", Settings("first", true), source);
        ManagementClient? manager = null;
        async Task Connect()
        {
            await Until(async () => {
                try { manager = await ManagementClient.ConnectAsync(addons); return true; }
                catch (Exception error) when (error is IOException or TimeoutException) { return false; }
            });
        }
        Task<JsonNode?> Call(int index, string method, JsonObject? extra = null)
        {
            extra ??= new(); extra["id"] = packages[index].Manifest.Id;
            return manager!.CallAsync(method, extra);
        }
        Task<JsonNode?> Action(int index, string action) => Call(index, "addons.action", new() { ["action"] = action });
        async Task<JsonObject> Sample(int index, Func<JsonObject, bool>? accepts = null)
        {
            JsonObject? latest = null;
            await Until(async () => {
                latest = (JsonObject)(await Action(index, "latest"))!;
                Require(latest["error"] is null, "Sample failed: " + latest);
                return latest["frameId"] is not null && (accepts?.Invoke(latest) ?? true);
            });
            return latest!;
        }
        static async Task<bool> HasTap(NativeLifecycleChecks.TestPlayer player)
            => ((JsonArray)(await player.CommandAsync("get_property", "vf"))!).Any(f => f?["label"]?.GetValue<string>() == "ajn-player-sample");
        try
        {
            await first.ConnectAsync(); await Connect();
            await Call(0, "addons.start");
            await Until(async () => (await Action(0, "list")) is JsonArray a && a.Count == 1);
            Require(!await HasTap(first), "No observer may create GPU sample work.");
            await using var second = new NativeLifecycleChecks.TestPlayer(root, data, output, "second", "mpvnet.exe", Settings("second", false), source);
            await second.ConnectAsync();
            await Until(async () => (await Action(0, "list")) is JsonArray a && a.Count == 2);
            await Call(1, "addons.configure", new() { ["changes"] = new JsonObject { ["playerNumber"] = 2L, ["width"] = 32L, ["height"] = 18L, ["fps"] = 12L } });
            await Call(1, "addons.start");
            Require((await Action(0, "subscribe"))?["subscriptionId"] is not null, "First subscription failed.");
            Require((await Action(1, "subscribe"))?["subscriptionId"] is not null, "Second subscription failed.");
            var firstFrame = await Sample(0); var secondFrame = await Sample(1);
            Require(firstFrame["sourceWidth"]!.GetValue<int>() == 960 && firstFrame["sourceHeight"]!.GetValue<int>() == 720 &&
                secondFrame["sourceWidth"]!.GetValue<int>() == 960 && secondFrame["sourceHeight"]!.GetValue<int>() == 720, "Expected real DirectML 2x output in both players.");
            Require(firstFrame["bytes"]!.GetValue<int>() == 64 * 36 * 4 && secondFrame["bytes"]!.GetValue<int>() == 32 * 18 * 4 &&
                firstFrame["checksum"]!.GetValue<long>() > 64 * 36 * 255 && secondFrame["checksum"]!.GetValue<long>() > 32 * 18 * 255, "Expected nonblack negotiated picture samples.");
            evidence.Add(new() { ["twoRealPlayers"] = true, ["firstRenderer"] = "gpu-next/d3d11", ["first"] = firstFrame, ["second"] = secondFrame });
            string epoch = firstFrame["epoch"]!.GetValue<string>();
            await first.CommandAsync("seek", "10", "absolute+exact");
            var sought = await Sample(0, frame => frame["epoch"]!.GetValue<string>() != epoch && frame["ptsSeconds"]!.GetValue<double>() >= 9.9);
            string nextSource = Path.Combine(output, "next-video.mp4"); File.Copy(source, nextSource);
            await first.CommandAsync("loadfile", nextSource, "replace");
            var loaded = await Sample(0, frame => frame["epoch"]!.GetValue<string>() != sought["epoch"]!.GetValue<string>() && frame["ptsSeconds"]!.GetValue<double>() < 5);
            evidence.Add(new() { ["seek"] = sought, ["fileChange"] = loaded });
            await Call(2, "addons.configure", new() { ["changes"] = new JsonObject { ["width"] = 80L, ["height"] = 45L, ["fps"] = 20L } });
            await Call(2, "addons.start"); await Action(2, "subscribe");
            var thirdFrame = await Sample(2); var resized = await Sample(0);
            Require(thirdFrame["bytes"]!.GetValue<int>() == 80 * 45 * 4 && resized["bytes"]!.GetValue<int>() == 64 * 36 * 4, "Shared consumers lost independent dimensions.");
            var filters = (JsonArray)(await first.CommandAsync("get_property", "vf"))!;
            Require(filters.Count(f => f?["label"]?.GetValue<string>() == "ajn-player-sample") == 1, "Sharing must retain one native sample branch.");
            await Call(0, "addons.stop");
            var survivor = await Sample(2, f => f["frameId"]!.GetValue<string>() != thirdFrame["frameId"]!.GetValue<string>());
            await Action(2, "unsubscribe"); await Until(async () => !await HasTap(first));
            double before = await first.PositionAsync(); await Until(async () => await first.PositionAsync() > before + .4);
            await Call(2, "addons.stop");
            evidence.Add(new() { ["sharedObserver"] = thirdFrame, ["independentResizing"] = resized, ["survivesOtherAddonStop"] = survivor, ["lastUnsubscribeRemovesTap"] = true });
            // Terminate only the host loaded from this unique disposable test root.
            // The Windows Job must close its Wasm children; neither player belongs
            // to that job, and stale control leases must stop sampling.
            uint hostId;
            using (var probe = new NamedPipeClientStream(".", ManagementServer.PipeName(addons), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                await probe.ConnectAsync(5000);
                Require(GetNamedPipeServerProcessId(probe.SafePipeHandle, out hostId), "Could not identify the test management host.");
            }
            using var host = Process.GetProcessById(checked((int)hostId));
            Require(string.Equals(host.MainModule?.FileName, Path.Combine(root, "addon-host", "ajn-addon.exe"), StringComparison.OrdinalIgnoreCase), "Test host path changed.");
            double secondBefore = await second.PositionAsync();
            host.Kill(); await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await manager!.DisposeAsync(); manager = null;
            await Until(async () => !await HasTap(second) && await second.PositionAsync() > secondBefore + .4);
            await Connect();
            var restarted = await manager!.ListAsync();
            Require(restarted.All(a => a?["running"]?.GetValue<bool>() == false), "Manual addons must not restart silently after host failure.");
            evidence.Add(new() { ["hostFailurePreservesBothPlayers"] = true, ["staleObservationRemoved"] = true, ["manualWorkersRemainStopped"] = true });
            await second.QuitAsync(); await first.QuitAsync();
            await manager.DisposeAsync(); manager = null;
            await Until(() => Task.FromResult(!Process.GetProcessesByName("ajn-addon").Concat(Process.GetProcessesByName("ajn-addon-launcher")).Any(p => {
                using (p) { try { return string.Equals(Path.GetDirectoryName(p.MainModule?.FileName), Path.Combine(root, "addon-host"), StringComparison.OrdinalIgnoreCase); } catch { return false; } }
            })), 45);
            foreach (var (name, digest) in expectedSettings) Require(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, "portable_config", name))).SequenceEqual(digest), "Test changed portable settings: " + name);
            evidence.Add(new() { ["allTestHelpersExit"] = true, ["portableSettingsUnchanged"] = true });
        }
        finally
        {
            if (manager is not null)
            {
                foreach (var package in packages)
                {
                    try { await manager.CallAsync("addons.stop", new() { ["id"] = package.Manifest.Id }); }
                    catch (Exception error) when (error is IOException or TimeoutException or ManagementException) { }
                }
                await manager.DisposeAsync();
            }
        }
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}
