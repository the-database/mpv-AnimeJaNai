using System.Diagnostics;
using System.Runtime.InteropServices;

// The first start after the switch is mpv.net (the old updater relaunched it). When --check says
// PLAYER_MIGRATED, animejanai_update.lua moves the user to the launcher, from mpv.net only.
// A copy of the release tree made of hard links (the user's install is not used), its own config
// dir, and this test program as AnimeJaNaiUpdater.exe printing PLAYER_MIGRATED.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
sealed partial class WindowsTests
{
    void HandOffFromMpvNet()
    {
        KillPlayers();
        string root = Path.Combine(Path.GetTempPath(), "ajn-handoff-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            LinkTree(tree, root);
            string config = Path.Combine(root, "portable_config");
            Directory.CreateDirectory(Path.Combine(config, "scripts"));
            string repo = RepoConfig();
            File.Copy(Path.Combine(repo, "scripts", "animejanai_update.lua"), Path.Combine(config, "scripts", "animejanai_update.lua"));
            CopyDir(Path.Combine(repo, "animejanai-ui"), Path.Combine(config, "animejanai-ui"));
            CopyDir(Path.Combine(repo, "animejanai-ui-compat"), Path.Combine(config, "animejanai-ui-compat"));
            File.WriteAllText(Path.Combine(config, "mpv.conf"), "pause=yes\nkeep-open=yes\n");
            // This program as the updater: the apphost finds Launcher.dll next to it.
            string self = Path.GetDirectoryName(Environment.ProcessPath)!;
            File.Delete(Path.Combine(root, "AnimeJaNaiUpdater.exe"));
            File.Copy(Environment.ProcessPath!, Path.Combine(root, "AnimeJaNaiUpdater.exe"));
            foreach (var f in Directory.GetFiles(self, "Launcher.*").Where(f => !f.EndsWith(".exe")))
            {
                File.Copy(f, Path.Combine(root, Path.GetFileName(f)), true);
            }
            var env = new Dictionary<string, string> { ["AJN_TEST_CHECK_OUTPUT"] = "PLAYER_MIGRATED\nUP_TO_DATE 1.0" };
            string name = NameFor(config);
            const string clip = "av://lavfi:testsrc=d=600";

            // Plain mpv: no hand-off.
            var plain = StartIn(root, Path.Combine(root, "mpv.exe"), [$"--config-dir={config}", "--really-quiet", clip], env);
            Thread.Sleep(4000);
            Check(!plain.HasExited && Ipc.Connect(name, 500) is null, "plain mpv stays put (no mpvnet client)");
            if (!plain.HasExited) plain.Kill();

            // mpv.net: hands off to the launcher and closes.
            var net = StartIn(root, Path.Combine(root, "mpvnet.exe"), [clip], env);
            Ipc? ipc = null;
            WaitFor(() => (ipc = Ipc.Connect(name, 200)) is not null, 20000);
            Check(ipc is not null, "mpv.net hands off: the launcher's player answers");
            if (ipc is not null)
            {
                WaitFor(() => ipc.TryGet("user-data/animejanai_ui/loaded")?.GetValue<bool>() == true, 10000);
                Check(ipc.TryGet("path")?.GetValue<string>() == clip, $"same file in the new player ({ipc.TryGet("path")})");
                Check(ipc.TryGet("user-data/animejanai_ui/loaded")?.GetValue<bool>() == true, "with the interface");
                ipc.TrySend("quit");
                ipc.Dispose();
            }
            Check(net.WaitForExit(10000), "mpv.net closes");
            if (!net.HasExited) net.Kill();
        }
        finally
        {
            foreach (var p in Process.GetProcesses().Where(p => p.ProcessName is "mpv" or "mpvnet"))
            {
                try
                {
                    if (p.MainModule?.FileName.StartsWith(root, StringComparison.OrdinalIgnoreCase) == true) p.Kill();
                }
                catch (Exception) { }
            }
            Thread.Sleep(500);
            try { Directory.Delete(root, true); } catch (Exception) { }
        }
    }

    Process StartIn(string dir, string exe, string[] a, Dictionary<string, string> env)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = dir };
        foreach (var x in a) psi.ArgumentList.Add(x);
        foreach (var (k, v) in env) psi.Environment[k] = v;
        psi.Environment.Remove("ANIMEJANAI_CONFIG_DIR");
        return Process.Start(psi)!;
    }

    /// A copy of `from` made of hard links (instant, no extra space), without its portable_config.
    static void LinkTree(string from, string to)
    {
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(from, dir);
            if (!rel.StartsWith("portable_config")) Directory.CreateDirectory(Path.Combine(to, rel));
        }
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(from, file);
            if (rel.StartsWith("portable_config")) continue;
            if (!CreateHardLinkW(Path.Combine(to, rel), file, 0)) File.Copy(file, Path.Combine(to, rel));
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLinkW(string link, string existing, nint security);
}
