using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Management;

internal static class NativeLifecycleChecks
{
    public static async Task RunAsync(string root, string output, List<JsonObject> evidence, string playerExecutable = "mpv.exe")
    {
        string data = Path.Combine(output, "data"), addonData = Path.Combine(data, "addons");
        using (var stream = File.OpenRead(Path.Combine(root, "addon-host", "ajn-addon-launcher.exe")))
        using (var image = new System.Reflection.PortableExecutable.PEReader(stream))
            if (image.PEHeaders.PEHeader!.Subsystem != System.Reflection.PortableExecutable.Subsystem.WindowsGui) throw new Exception("The login launcher must not open a console.");
        await using (var empty = new TestPlayer(root, Path.Combine(output, "empty-data"), output, "no-addons", playerExecutable))
        {
            await empty.ConnectAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (await empty.PositionAsync() < 1) await Task.Delay(100, deadline.Token);
            if (CountHostProcesses(root) != 0) throw new Exception("A player with no installed addons must not launch addon processes.");
            await empty.QuitAsync();
        }
        var loginStart = new ProcessStartInfo(Path.Combine(root, "addon-host", "ajn-addon-launcher.exe")) { UseShellExecute = false, CreateNoWindow = true };
        loginStart.ArgumentList.Add("login"); loginStart.ArgumentList.Add(Path.Combine(output, "unregistered-login-data"));
        using (var login = Process.Start(loginStart)!)
        {
            await login.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (login.ExitCode != 0 || Directory.Exists(Path.Combine(output, "unregistered-login-data"))) throw new Exception("A launcher without login opt-in must exit without creating a host or data.");
        }
        evidence.Add(new() { ["check"] = "no addon processes for empty installations; console-free launcher; no login activation without opt-in" });
        var original = AddonPackage.Load(Path.Combine(root, "addon-development", "counter.ajnaddon"));
        string module = Path.Combine(output, "counter.wasm"); original.WriteModule(module);
        var package = AddonPackage.Create(original.Manifest with { Id = "org.animejanai.playertest", Activation = ["manual", "on_player"] }, File.ReadAllBytes(module));
        new AddonRegistry(addonData).Install(package, package.Manifest.Permissions);
        var broken = AddonPackage.Create(new AddonManifest { SchemaVersion = 1, Id = "org.animejanai.brokenplayertest", Name = "Intentional startup failure",
            Version = "1.0.0", Api = new() { Major = 1, MinMinor = 0 }, Activation = ["on_player"], Permissions = [], ModuleSha256 = new string('0', 64) }, [0, 97, 115, 109, 1, 0, 0, 0]);
        new AddonRegistry(addonData).Install(broken, []);
        await using var first = new TestPlayer(root, data, output, "first", playerExecutable);
        await using var second = new TestPlayer(root, data, output, "second", playerExecutable);
        ManagementClient? manager = null;
        try
        {
            using var connectDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            while (manager is null)
            {
                try { manager = await ManagementClient.ConnectAsync(addonData, connectDeadline.Token); }
                catch (Exception error) when (error is IOException or TimeoutException) { await Task.Delay(100, connectDeadline.Token); }
            }
            async Task<JsonObject> Status() => (JsonObject)(await manager.ListAsync()).Single(p => p!["id"]!.GetValue<string>() == package.Manifest.Id)!;
            async Task Until(Func<Task<bool>> ready, int seconds = 15)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
                while (!await ready()) await Task.Delay(100, deadline.Token);
            }
            async Task<long> Starts() => (await manager.CallAsync("addons.action", new() { ["id"] = package.Manifest.Id, ["action"] = "status" }))!["starts"]!.GetValue<long>();
            await Until(async () => (await Status())["running"]!.GetValue<bool>());
            await first.ConnectAsync(); await second.ConnectAsync();
            await Until(async () => await first.PositionAsync() > 1 && await second.PositionAsync() > 1);
            if (await Starts() != 1) throw new Exception("Concurrent players must share one actual Wasm instance.");
            var failed = (await manager.ListAsync()).Single(p => p!["id"]!.GetValue<string>() == broken.Manifest.Id)!;
            if (failed["running"]!.GetValue<bool>() || failed["error"] is null) throw new Exception("Expected the intentionally failing addon to be contained.");
            evidence.Add(new() { ["check"] = "two real players, one Wasm worker, simultaneous host launch, failing addon contained", ["firstSeconds"] = await first.PositionAsync(), ["secondSeconds"] = await second.PositionAsync() });
            double before = await second.PositionAsync(); await first.KillAsync();
            await Until(async () => await second.PositionAsync() > before + 1);
            if (!(await Status())["running"]!.GetValue<bool>() || await Starts() != 1) throw new Exception("Unexpected player exit stopped shared work.");
            await second.QuitAsync(); await Until(async () => !(await Status())["running"]!.GetValue<bool>());
            evidence.Add(new() { ["check"] = "forced first-player exit preserves playback; last-player exit releases addon" });
            await using (var third = new TestPlayer(root, data, output, "third", playerExecutable))
            {
                await third.ConnectAsync(); await Until(async () => (await Status())["running"]!.GetValue<bool>());
                if (await Starts() != 2) throw new Exception("A new player should start a fresh worker with preserved storage.");
                await manager.CallAsync("addons.start", new() { ["id"] = package.Manifest.Id });
                await third.QuitAsync();
                await Task.Delay(600);
                if (!(await Status())["running"]!.GetValue<bool>() || await Starts() != 2) throw new Exception("Manual work must outlive players.");
                await manager.CallAsync("addons.stop", new() { ["id"] = package.Manifest.Id });
            }
            evidence.Add(new() { ["check"] = "later player preserves data; explicit manual activation outlives all players" });
            await manager.DisposeAsync(); manager = null;
            // Only processes loaded from this unique test installation are counted.
            await Until(() => Task.FromResult(CountHostProcesses(root) == 0), seconds: 45);
            evidence.Add(new() { ["check"] = "all player bridges and idle host exit" });
        }
        finally
        {
            if (manager is not null)
            {
                try { await manager.CallAsync("addons.stop", new() { ["id"] = package.Manifest.Id }); }
                finally { await manager.DisposeAsync(); }
            }
        }
    }

    private static int CountHostProcesses(string root)
    {
        int count = 0;
        foreach (var process in Process.GetProcessesByName("ajn-addon").Concat(Process.GetProcessesByName("ajn-addon-launcher")))
        {
            using (process)
            {
                try { if (string.Equals(Path.GetDirectoryName(process.MainModule?.FileName), Path.Combine(root, "addon-host"), StringComparison.OrdinalIgnoreCase)) count++; }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return count;
    }

    internal sealed class TestPlayer : IAsyncDisposable
    {
        private readonly Process process;
        private readonly string pipeName = "AJN.LifecycleTest." + Guid.NewGuid().ToString("N");
        private NamedPipeClientStream? pipe;
        private StreamReader? reader;
        private StreamWriter? writer;
        private int sequence;
        internal int Id => process.Id;
        public TestPlayer(string root, string data, string output, string name, string executable, string[]? extra = null, string? source = null)
        {
            // mpv.net persists its own settings even with mpv's --no-config.
            // Give each player a private directory so neither the package nor
            // another concurrent player supplies or receives test settings.
            string config = Path.Combine(output, name + "-config");
            Directory.CreateDirectory(config);
            var info = new ProcessStartInfo(Path.Combine(root, executable)) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
            info.Environment["MPVNET_HOME"] = config;
            if (executable == "mpvnet.exe") {
                info.ArgumentList.Add("--process-instance=multi"); info.ArgumentList.Add("--auto-load-folder=no");
                info.ArgumentList.Add("--force-window=no"); info.ArgumentList.Add("--window-minimized=yes");
            }
            info.Environment["ANIMEJANAI_ROOT"] = root; info.Environment["ANIMEJANAI_DATA_DIR"] = data;
            foreach (string value in new[] { "--config-dir=" + config, "--no-config", "--load-scripts=no", "--vo=null", "--ao=null", "--hwdec=no", "--keep-open=yes", "--idle=yes",
                "--input-terminal=no", "--terminal=no", "--input-ipc-server=\\\\.\\pipe\\" + pipeName,
                "--script=" + Path.Combine(root, "portable_config", "scripts", "animejanai_addons.lua"),
                "--log-file=" + Path.Combine(output, name + "-mpv.log") }) info.ArgumentList.Add(value);
            foreach (string value in extra ?? []) info.ArgumentList.Add(value);
            info.ArgumentList.Add(source ?? Path.Combine(root, "animejanai", "benchmarks", "480x360.mp4"));
            process = Process.Start(info) ?? throw new Exception("Could not start test player.");
            _ = Drain(process.StandardOutput); _ = Drain(process.StandardError);
        }
        private static async Task Drain(StreamReader stream)
        { try { char[] buffer = new char[1024]; while (await stream.ReadAsync(buffer) != 0) { } } catch (IOException) { } }
        public async Task ConnectAsync()
        {
            pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(15000);
            reader = new(pipe, leaveOpen: true); writer = new(pipe, leaveOpen: true) { AutoFlush = true };
        }
        public async Task<double> PositionAsync()
            => (await CommandAsync("get_property", "time-pos"))?.GetValue<double>() ?? 0;
        internal async Task<JsonNode?> CommandAsync(params string[] command)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); int request = ++sequence;
            await writer!.WriteLineAsync(new JsonObject { ["command"] = new JsonArray(command.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()), ["request_id"] = request }.ToJsonString());
            while (true)
            {
                string line = await reader!.ReadLineAsync(deadline.Token) ?? throw new IOException("Test player disconnected.");
                var result = JsonNode.Parse(line)!;
                if (result["request_id"]?.GetValue<int>() == request)
                {
                    if (result["error"]?.GetValue<string>() != "success") throw new IOException("Player test command failed: " + result["error"]);
                    return result["data"]?.DeepClone();
                }
            }
        }
        public async Task KillAsync() { if (!process.HasExited) process.Kill(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        public async Task QuitAsync()
        {
            if (process.HasExited) return;
            await writer!.WriteLineAsync("{\"command\":[\"quit\"]}"); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (process.ExitCode != 0) throw new Exception("Test player exited unexpectedly.");
        }
        public async ValueTask DisposeAsync()
        {
            try { await KillAsync(); }
            finally { writer?.Dispose(); reader?.Dispose(); pipe?.Dispose(); process.Dispose(); }
        }
    }
}
