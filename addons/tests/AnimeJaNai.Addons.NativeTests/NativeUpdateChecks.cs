using System.Diagnostics;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Management;
using AnimeJaNai.Updates;

internal static class NativeUpdateChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Copy(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (string child in Directory.EnumerateDirectories(source)) Copy(child, Path.Combine(destination, Path.GetFileName(child)));
    }
    private static ProcessStartInfo Command(string executable, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }
    private static Process MainHost(string installation)
    {
        string expected = Path.GetFullPath(Path.Combine(installation, "addon-host/ajn-addon.exe"));
        var processes = Process.GetProcessesByName("ajn-addon");
        try
        {
            var owned = new List<Process>();
            foreach (var process in processes)
                try { if (process.MainModule?.FileName.Equals(expected, StringComparison.OrdinalIgnoreCase) == true) owned.Add(process); }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            // The management host starts before the worker processes it owns.
            var tracked = Process.GetProcessById(owned.OrderBy(p => p.StartTime).First().Id);
            // Open and retain the handle before exit; a PID-only object cannot
            // retrieve an exit code after Windows removes the process record.
            try { _ = tracked.Handle; return tracked; }
            catch { tracked.Dispose(); throw; }
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    private static async Task RunCommandAsync(string log, string executable, params string[] arguments)
    {
        using var process = Process.Start(Command(executable, arguments)) ?? throw new IOException("Test process did not start");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        File.WriteAllText(log, await stdout + await stderr);
        Check(process.ExitCode == 0, $"Process exited {process.ExitCode}; see {log}");
    }
    private static async Task ProcessRecoveryAsync(string packageRoot, string output, string dotnet, List<JsonObject> evidence)
    {
        string updater = Path.Combine(packageRoot, "AnimeJaNaiUpdater.exe");
        Check(File.Exists(updater), "The packaged updater is required for update tests");
        string source = Path.Combine(output, "interruption-source"), target = Path.Combine(output, "interruption-target");
        Directory.CreateDirectory(source); Directory.CreateDirectory(target);
        foreach (string name in new[] { "one.txt", "two.txt" })
        {
            File.WriteAllText(Path.Combine(target, name), "old"); File.WriteAllText(Path.Combine(source, name), "new");
        }
        File.WriteAllText(Path.Combine(source, "created.txt"), "created");
        string ready = Path.Combine(output, "interruption-ready");
        using (var child = Process.Start(Command(dotnet, typeof(NativeUpdateChecks).Assembly.Location,
            "--interrupt-update-helper", source, target, ready)) ?? throw new IOException("Test child did not start"))
        {
            var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
            try
            {
                var wait = Stopwatch.StartNew();
                while (!File.Exists(ready) && !child.HasExited && wait.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(20);
                Check(File.Exists(ready), "Child did not reach a prepared journal and first published file");
                Check(InstallationActivity.Pending(target), "Interrupted update did not retain activation exclusion");
            }
            finally
            {
                // Terminate only this test-owned process after its flushed journal.
                if (!child.HasExited) child.Kill(true);
                await child.WaitForExitAsync();
                File.WriteAllText(Path.Combine(output, "interruption-child.log"), await stdout + await stderr);
            }
        }
        await RunCommandAsync(Path.Combine(output, "interruption-recovery.log"), updater, "--recover-target", target);
        Check(new[] { "one.txt", "two.txt" }.All(n => File.ReadAllText(Path.Combine(target, n)) == "old") &&
            !File.Exists(Path.Combine(target, "created.txt")) && !InstallationActivity.Pending(target), "Recovery after process termination was incomplete");
        evidence.Add(new() { ["name"] = "real updater recovers after interrupted transaction process is terminated", ["passed"] = true });

        string self = Path.Combine(output, "self-update-target"), replacement = Path.Combine(output, "self-update-source");
        Directory.CreateDirectory(self); Directory.CreateDirectory(replacement);
        foreach (string directory in new[] { self, replacement }) File.Copy(updater, Path.Combine(directory, "AnimeJaNaiUpdater.exe"));
        File.WriteAllText(Path.Combine(self, "managed.txt"), "old"); File.WriteAllText(Path.Combine(replacement, "managed.txt"), "new");
        string installedUpdater = Path.Combine(self, "AnimeJaNaiUpdater.exe");
        await RunCommandAsync(Path.Combine(output, "self-update.log"), installedUpdater, "--apply-staged", self, replacement);
        Check(File.ReadAllText(Path.Combine(self, "managed.txt")) == "new" && File.Exists(installedUpdater) && !InstallationActivity.Pending(self), "Running updater did not replace itself and commit");
        await RunCommandAsync(Path.Combine(output, "self-update-cleanup.log"), installedUpdater, "--recover");
        Check(!Directory.Exists(Path.Combine(self, InstallationActivity.DirectoryName)), "Previous updater image was not cleaned up on the next run");
        evidence.Add(new() { ["name"] = "packaged updater replaces its own running image and next run cleans it up", ["passed"] = true });
    }
    public static async Task RunAsync(string packageRoot, string output, string dotnet, List<JsonObject> evidence)
    {
        await ProcessRecoveryAsync(packageRoot, output, dotnet, evidence);
        string one = Path.Combine(output, "installation-one"), two = Path.Combine(output, "installation-two");
        string dataOne = Path.Combine(output, "external-data-one"), dataTwo = Path.Combine(output, "external-data-two");
        foreach (string installation in new[] { one, two }) Copy(Path.Combine(packageRoot, "addon-host"), Path.Combine(installation, "addon-host"));
        // An overlay omits native/Manager dependencies; validation must resolve
        // their exact inventory hashes from the installed package.
        string inventory = Path.Combine(packageRoot, AddonUpdateTransaction.PackageManifest);
        var listed = JsonNode.Parse(File.ReadAllText(inventory))!["files"]!.AsObject();
        foreach (var entry in listed)
        {
            string destination = Path.GetFullPath(Path.Combine(one, entry.Key));
            Check(destination.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(one)) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase), "Package inventory escaped the test installation");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(packageRoot, entry.Key), destination, true);
        }
        File.Copy(inventory, Path.Combine(one, AddonUpdateTransaction.PackageManifest));
        var package = AddonPackage.Load(Path.Combine(packageRoot, "addon-development", "counter.ajnaddon"));
        foreach (string data in new[] { dataOne, dataTwo }) new AddonRegistry(data).Install(package, ["log.write", "storage.read", "storage.write"]);
        async Task<ManagementClient> Connect(string install, string data)
        {
            var client = await ManagementClient.ConnectOrStartAsync(data, Path.Combine(install, "addon-host/ajn-addon.exe"),
                Path.Combine(install, "addon-host/runtime/wasmtime.exe"));
            await client.CallAsync("addons.start", new() { ["id"] = package.Manifest.Id });
            return client;
        }
        ManagementClient? first = null, second = null;
        string activeOne = one;
        try
        {
            first = await Connect(one, dataOne);
            second = await Connect(two, dataTwo);
            Check(new AddonStorage(dataOne, package.Manifest.Id).Get("starts")!.GetValue<int>() == 1, "First real addon did not start");
            string staged = Path.Combine(output, "replacement"); Directory.CreateDirectory(staged);
            File.WriteAllText(Path.Combine(staged, "managed.txt"), "updated");
            File.Copy(inventory, Path.Combine(staged, AddonUpdateTransaction.PackageManifest));
            Directory.CreateDirectory(Path.Combine(staged, "addon-host"));
            File.Copy(Path.Combine(packageRoot, "addon-host/ajn-addon.dll"), Path.Combine(staged, "addon-host/ajn-addon.dll"));
            bool excluded = false;
            using var originalHost = MainHost(one);
            var timer = Stopwatch.StartNew();
            await AddonUpdateTransaction.ApplyAsync(staged, one, new HashSet<string>(), afterPublish: _ =>
            {
                try { using var contender = InstallationActivity.Acquire(one); }
                catch (ManagementException e) when (e.Code == "update_in_progress") { excluded = true; }
            });
            timer.Stop();
            // Large native inventories spend time hashing before shutdown.
            // Observe the host's actual normal exit instead of inferring forced
            // termination from the duration of the entire update transaction.
            Check(originalHost.HasExited && originalHost.ExitCode == 0, "Current packaged host did not exit normally for the update");
            Check(excluded, "Concurrent startup was allowed during replacement");
            Check((await second.ListAsync()).Count == 1, "Updating one installation stopped the other");
            Check(new AddonStorage(dataOne, package.Manifest.Id).Get("starts")!.GetValue<int>() == 1, "Update changed private data");
            await using (var restarted = await Connect(one, dataOne))
            {
                Check(new AddonStorage(dataOne, package.Manifest.Id).Get("starts")!.GetValue<int>() == 2, "Restart lost durable state");
                await AddonUpdateTransaction.PrepareUninstallAsync(one);
            }
            // These are new directories owned by this test. Validate both paths
            // before moving the stopped installation; external data stays put.
            string moved = Path.GetFullPath(Path.Combine(output, "moved-installation"));
            string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(output)) + Path.DirectorySeparatorChar;
            Check(Path.GetFullPath(one).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && moved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase), "Move escaped test output");
            await AddonUpdateTransaction.RecoverAsync(one); // Cancel this simulated uninstall before testing a move.
            Directory.Move(one, moved);
            activeOne = moved;
            await using (var afterMove = await Connect(moved, dataOne))
            {
                Check(new AddonStorage(dataOne, package.Manifest.Id).Get("starts")!.GetValue<int>() == 3, "Move lost shared data");
                await AddonUpdateTransaction.PrepareUninstallAsync(moved);
            }
            Check(Directory.Exists(dataOne) && (await second.ListAsync()).Count == 1, "Uninstall preparation affected external data or the other install");
            evidence.Add(new() { ["name"] = "packaged overlay, unchanged dependency inventory, restart, moved installation and uninstall preparation",
                ["passed"] = true, ["updateMilliseconds"] = timer.Elapsed.TotalMilliseconds, ["externalDataPreserved"] = true });
        }
        finally
        {
            try { await AddonUpdateTransaction.PrepareUninstallAsync(activeOne); }
            finally
            {
                await AddonUpdateTransaction.PrepareUninstallAsync(two);
                if (first is not null) await first.DisposeAsync();
                if (second is not null) await second.DisposeAsync();
            }
        }
    }
}
