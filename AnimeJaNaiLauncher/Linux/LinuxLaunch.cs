using System.Runtime.Versioning;

namespace AnimeJaNai.Launcher;

/// Linux: the launcher becomes the player (execve), so there is no window to tag and no process
/// left to wait for it; the hand-off works over the player's Unix socket.
static partial class Launch
{
    [SupportedOSPlatform("linux")]
    static int RunLinux(Request req)
    {
        // Started from a desktop entry or a file manager there is no terminal: mpv's pseudo-gui
        // mode (a window even with no file, no terminal output), as the Windows GUI build does.
        // From a terminal, plain mpv behaviour, as the old shell launcher had.
        bool gui = !Posix.IsTerminal(1) && !Posix.IsTerminal(2);
        if (req.NoUi)
        {
            return LinuxExec(PlayerArguments(req, null, gui));
        }
        if (req.HasOptions || req.NewInstance)
        {
            // Our pid is the player's pid after the exec.
            return LinuxExec(PlayerArguments(req, Install.IpcServer($"{Install.InstanceName}-{Environment.ProcessId}"), gui));
        }
        return SingleInstanceLinux(req, gui);
    }

    /// Several launchers at once (a file manager opening a multi-file selection starts one per
    /// file) must end in one player. Each holds an flock on <name>.lock while it decides:
    /// - a player answers on the socket: forward the files to it;
    /// - else a player an earlier launcher exec'd is still starting (<name>.starting names its pid,
    ///   which after the exec is the player's): wait, still holding the lock, until its socket
    ///   answers, then forward;
    /// - else write <name>.starting with our pid and exec the player.
    /// The lock descriptor is close-on-exec, so the exec itself releases it: no process stays
    /// behind to hold it, and the next launcher finds the marker and waits for the socket.
    [SupportedOSPlatform("linux")]
    static int SingleInstanceLinux(Request req, bool gui)
    {
        string name = Install.InstanceName;
        string starting = Path.Combine(Install.RuntimeDir, name + ".starting");
        using var held = Posix.Lock(Path.Combine(Install.RuntimeDir, name + ".lock"), 15000);
        Log.Write(held is null ? "launch lock not free after 15 s, going ahead" : "launch lock held");
        bool recent = LastLaunchWithin(name, AppendWindowMs);
        while (true)
        {
            using (var ipc = MpvIpc.TryConnect(name, 50))
            {
                if (ipc is not null)
                {
                    Log.Write($"forwarding {req.Files.Count} file(s), {(recent ? "append" : "replace")}");
                    Forward(ipc, req.Files, appendAll: recent);
                    MarkLaunch(name);
                    return 0; // no focus call: Wayland has none and X11 window managers refuse it
                }
            }
            if (StartingPlayer(starting) is not int pid)
            {
                break;
            }
            bool up = MpvIpc.WaitForServer(name, 10000, () => Posix.Alive(pid));
            Log.Write($"waited for the starting player {pid}: {(up ? "up" : "not up")}");
            TryDelete(starting);
            if (!up)
            {
                break;
            }
            recent = true; // its launch was just now: append behind its file
        }
        try
        {
            File.WriteAllText(starting, Environment.ProcessId.ToString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Write($"no starting marker: {e.Message}");
        }
        MarkLaunch(name);
        return LinuxExec(PlayerArguments(req, Install.IpcServer(name), gui));
    }

    /// The pid in the starting marker, when that process is alive and the marker is fresh.
    [SupportedOSPlatform("linux")]
    static int? StartingPlayer(string marker)
    {
        try
        {
            if (!File.Exists(marker) || (DateTime.UtcNow - File.GetLastWriteTimeUtc(marker)).TotalSeconds > 15)
            {
                return null;
            }
            return int.TryParse(File.ReadAllText(marker).Trim(), out int pid) && pid != Environment.ProcessId && Posix.Alive(pid) ? pid : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    [SupportedOSPlatform("linux")]
    static int LinuxExec(List<string> args)
    {
        string exe = Install.Player(console: false);
        if (!File.Exists(exe))
        {
            throw new FileNotFoundException($"the player is missing: {exe}");
        }
        Log.Write($"exec {exe} ({args.Count} arguments)");
        Posix.Exec(exe, args, LinuxEnv.PlayerEnvironment());
        return 2; // Exec comes back only by throwing
    }
}
