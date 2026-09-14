using System.Diagnostics;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Management;
using Microsoft.Win32;

internal static class NativeInstallerChecks
{
    private const string TestUninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{78315E8B-A449-4D53-940F-452A227D80AA}_is1";
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task RunAsync(string executable, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Installer process did not start");
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3)); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        Check(process.ExitCode == 0, $"Installer process exited {process.ExitCode}; inspect the setup log");
    }
    public static async Task RunAsync(string packageRoot, string output, string setup, List<JsonObject> evidence)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Installer checks require Windows");
        Check(FileVersionInfo.GetVersionInfo(setup).ProductName == "mpv-AnimeJaNai Installer Test", "Compile with /DInstallerTest; production installers must not run in this test");
        using (var existing = Registry.CurrentUser.OpenSubKey(TestUninstallKey))
            Check(existing is null, "An earlier installer test is still registered; uninstall that test before proceeding");
        string installation = Path.GetFullPath(Path.Combine(output, "installed"));
        Check(installation.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(output)) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase), "Test installation escaped its new output directory");
        string data = Path.Combine(output, "external-addon-data");
        string uninstaller = Path.Combine(installation, "unins000.exe");
        string suffix = Guid.NewGuid().ToString("N");
        string ownValue = "AnimeJaNai.Addons.InstallerTest.Own." + suffix;
        string otherValue = "AnimeJaNai.Addons.InstallerTest.Other." + suffix;
        string ownCommand = $"\"{Path.Combine(installation, "addon-host/ajn-addon-launcher.exe").Replace('/', '\\')}\" \"login\" \"{data}\"";
        string otherCommand = $"\"{Path.Combine(output, "another-installation/addon-host/ajn-addon-launcher.exe").Replace('/', '\\')}\" \"login\" \"{data}\"";
        async Task Install(string name) => await RunAsync(setup, "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-",
            "/NOICONS", "/TASKS=", "/MERGETASKS=!assocvideo,!desktopicon", "/DIR=" + installation, "/LOG=" + Path.Combine(output, name));
        async Task<ManagementClient> Start()
        {
            var client = await ManagementClient.ConnectOrStartAsync(data, Path.Combine(installation, "addon-host/ajn-addon.exe"),
                Path.Combine(installation, "addon-host/runtime/wasmtime.exe"));
            await client.CallAsync("addons.start", new() { ["id"] = "org.animejanai.counter" });
            return client;
        }
        try
        {
            await Install("fresh-install.log");
            Check(File.Exists(uninstaller) && File.Exists(Path.Combine(installation, "addon-package.json")), "Fresh addon installation is incomplete");
            string settings = Path.Combine(installation, "portable_config/mpv.conf");
            File.WriteAllText(settings, "# preserved installer test settings\nvolume=37\n");
            string localData = Directory.CreateDirectory(Path.Combine(installation, "animejanai/addons/data")).FullName;
            File.WriteAllText(Path.Combine(localData, "keep.txt"), "preserved");
            var package = AddonPackage.Load(Path.Combine(packageRoot, "addon-development/counter.ajnaddon"));
            new AddonRegistry(data).Install(package, ["log.write", "storage.read", "storage.write"]);
            await using (var first = await Start())
            {
                Check(new AddonStorage(data, package.Manifest.Id).Get("starts")!.GetValue<int>() == 1, "Installed host did not run the addon");
                await Install("reinstall-active-addon.log");
            }
            Check(File.ReadAllText(settings).Contains("volume=37") && File.ReadAllText(Path.Combine(localData, "keep.txt")) == "preserved", "Reinstall replaced user settings or addon data");
            await using (var second = await Start())
            {
                Check(new AddonStorage(data, package.Manifest.Id).Get("starts")!.GetValue<int>() == 2, "Reinstalled host lost external addon state");
                using (var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    run.SetValue(ownValue, ownCommand); run.SetValue(otherValue, otherCommand);
                }
                await RunAsync(uninstaller, "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=" + Path.Combine(output, "uninstall.log"));
            }
            var cleanup = Stopwatch.StartNew();
            while (Directory.Exists(installation) && cleanup.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(100);
            Check(!Directory.Exists(installation), "Uninstaller left its application tree behind");
            Check(new AddonStorage(data, package.Manifest.Id).Get("starts")!.GetValue<int>() == 2, "Uninstall removed externally stored addon data");
            using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                Check(run?.GetValue(ownValue) is null, "Uninstall retained its own login entry");
                Check(run?.GetValue(otherValue) as string == otherCommand, "Uninstall removed another installation's login entry");
            }
            using (var registered = Registry.CurrentUser.OpenSubKey(TestUninstallKey)) Check(registered is null, "Test installer registration remains after uninstall");
            evidence.Add(new() { ["name"] = "real Inno fresh install, active-addon reinstall, settings preservation and uninstall",
                ["passed"] = true, ["externalDataPreserved"] = true, ["otherInstallationLoginPreserved"] = true });
        }
        finally
        {
            try
            {
                if (File.Exists(uninstaller)) await RunAsync(uninstaller, "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=" + Path.Combine(output, "cleanup-uninstall.log"));
            }
            finally
            {
                using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                if (run?.GetValue(ownValue) as string == ownCommand) run.DeleteValue(ownValue);
                if (run?.GetValue(otherValue) as string == otherCommand) run.DeleteValue(otherValue);
            }
        }
    }
}
