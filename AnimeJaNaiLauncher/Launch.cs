using System.Text.RegularExpressions;

namespace AnimeJaNai.Launcher;

/// Opening files: hand them to the running player, or start one.
static partial class Launch
{
    /// Launches that arrive this soon after the previous one append instead of replacing:
    /// Explorer starts one launcher per selected file when several are opened at once.
    const int AppendWindowMs = 1500;

    sealed record Request(List<string> PlayerArgs, List<string> Files, bool HasOptions, bool NoUi, bool NewInstance);

    public static int Run(string[] args)
    {
        var req = Parse(args);
#if CONSOLE_BUILD
        // Terminal use: mpv.com in this console, waited for; output and exit code pass through.
        // No hand-off to a running player and no taskbar identity: that's what a terminal user expects.
        return Platform.RunConsole(Install.Player(console: true), PlayerArguments(req, ipcServer: null, gui: false));
#else
        if (req.NoUi)
        {
            return Platform.Start(Install.Player(console: false), PlayerArguments(req, null, gui: true), appId: false).ExitCode;
        }
        if (req.HasOptions || req.NewInstance)
        {
            return StartPlayer(req, $"{Install.InstanceName}-{Environment.ProcessId}");
        }
        return OpenSingleInstance(req);
#endif
    }

    static Request Parse(string[] args)
    {
        var playerArgs = new List<string>();
        var files = new List<string>();
        bool hasOptions = false, noUi = false, newInstance = false, onlyFiles = false;
        foreach (var a in args)
        {
            if (!onlyFiles && a == "--no-ui") { noUi = true; continue; }
            if (!onlyFiles && a == "--new-instance") { newInstance = true; continue; }
            if (!onlyFiles && a == "--") { onlyFiles = true; playerArgs.Add(a); continue; }
            if (!onlyFiles && a.Length > 1 && a[0] == '-')
            {
                hasOptions = true;
                playerArgs.Add(a);
                continue;
            }
            string target = NormalizeTarget(a);
            files.Add(target);
            playerArgs.Add(target);
        }
        return new Request(playerArgs, files, hasOptions, noUi, newInstance);
    }

    /// Files become absolute paths (a running player has another working folder); URLs and mpv's
    /// own schemes (av://, dvd://, edl://, ...) and "-" (stdin) pass through untouched.
    static string NormalizeTarget(string a)
    {
        if (a == "-" || UrlLike().IsMatch(a))
        {
            return a;
        }
        try
        {
            return Path.GetFullPath(a);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return a;
        }
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.\-]*://")]
    private static partial Regex UrlLike();

    static List<string> PlayerArguments(Request req, string? ipcServer, bool gui)
    {
        var a = new List<string> { $"--config-dir={Install.ConfigDir}" };
        if (ipcServer is not null)
        {
            a.Add($"--input-ipc-server={ipcServer}");
        }
        if (!req.NoUi && Directory.Exists(Install.UiDir))
        {
            a.Add($"--script={Install.UiDir}");
            string menu = Path.Combine(Install.UiDir, "menu.conf");
            if (File.Exists(menu))
            {
                a.Add($"--script-opts-append=select-menu_conf_path={menu}");
            }
            a.Add($"--script-opts-append=animejanai_ui-launcher={Install.GuiLauncherPath}");
            // Answers old "script-message-to mpvnet ..." bindings (its file name is its script name).
            string compat = Path.Combine(Install.ConfigDir, "animejanai-ui-compat", "mpvnet.lua");
            if (File.Exists(compat))
            {
                a.Add($"--script={compat}");
            }
        }
        if (gui)
        {
            a.Add("--player-operation-mode=pseudo-gui");
        }
        a.AddRange(req.PlayerArgs);
        return a;
    }

    /// The usual path: Explorer, the Start menu, a pinned button, a file association.
    static int OpenSingleInstance(Request req)
    {
        string name = Install.InstanceName;
        using var mutex = new Mutex(false, (OperatingSystem.IsWindows() ? @"Local\" : "") + name + "-launch");
        bool held = false;
        try
        {
            try
            {
                held = mutex.WaitOne(15000);
            }
            catch (AbandonedMutexException)
            {
                held = true;
            }
            bool recent = LastLaunchWithin(name, AppendWindowMs);
            using (var ipc = MpvIpc.TryConnect(name, 50))
            {
                if (ipc is not null)
                {
                    Log.Write($"forwarding {req.Files.Count} file(s), {(recent ? "append" : "replace")}");
                    Forward(ipc, req.Files, appendAll: recent);
                    MarkLaunch(name);
                    if (ipc.WindowId() is long wid)
                    {
                        Platform.BringToFront(wid);
                    }
                    return 0;
                }
            }
            var started = Platform.Start(Install.Player(console: false), PlayerArguments(req, Install.IpcServer(name), gui: true), appId: true);
            if (started.ExitCode != 0)
            {
                return started.ExitCode;
            }
            MarkLaunch(name);
            // Keep the next launcher of a multi-file open waiting until this player can take its file.
            bool up = MpvIpc.WaitForServer(name, 10000, started.IsAlive);
            Log.Write($"player started, IPC {(up ? "up" : "not up")}");
            if (held)
            {
                mutex.ReleaseMutex();
                held = false;
            }
            if (up)
            {
                TagWindow(name, started.IsAlive);
            }
            return 0;
        }
        finally
        {
            if (held)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    static int StartPlayer(Request req, string name)
    {
        var started = Platform.Start(Install.Player(console: false), PlayerArguments(req, Install.IpcServer(name), gui: true), appId: true);
        if (started.ExitCode == 0 && MpvIpc.WaitForServer(name, 10000, started.IsAlive))
        {
            TagWindow(name, started.IsAlive);
        }
        return started.ExitCode;
    }

    static void Forward(MpvIpc ipc, List<string> files, bool appendAll)
    {
        for (int i = 0; i < files.Count; i++)
        {
            ipc.Command("loadfile", files[i], i == 0 && !appendAll ? "replace" : "append-play");
        }
    }

    /// Gives the new player's window the launcher's taskbar identity and relaunch command, so a
    /// pin of it starts the launcher (with the interface), not plain mpv.exe.
    static void TagWindow(string name, Func<bool> alive)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 10000 && alive())
        {
            using var ipc = MpvIpc.TryConnect(name, 100);
            if (ipc?.WindowId() is long wid && wid != 0)
            {
                Platform.TagWindow(wid);
                Log.Write($"window {wid} tagged");
                return;
            }
            Thread.Sleep(50);
        }
        Log.Write("no window to tag");
    }

    static string StampPath(string name) => Path.Combine(Path.GetTempPath(), name + ".last-launch");

    static bool LastLaunchWithin(string name, int ms)
    {
        try
        {
            var t = File.GetLastWriteTimeUtc(StampPath(name));
            return File.Exists(StampPath(name)) && (DateTime.UtcNow - t).TotalMilliseconds < ms;
        }
        catch (IOException)
        {
            return false;
        }
    }

    static void MarkLaunch(string name)
    {
        try
        {
            File.WriteAllBytes(StampPath(name), []);
            File.SetLastWriteTimeUtc(StampPath(name), DateTime.UtcNow);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
