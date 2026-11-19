namespace AnimeJaNai.Launcher;

/// A started player process.
sealed record Started(int ExitCode, Func<bool> IsAlive);

/// Operating-system specific parts: Windows/*.cs and Linux/*.cs. On Linux the launcher execs the
/// player (Launch.RunLinux), so Start, RunConsole, BringToFront and TagWindow are Windows-only.
static partial class Platform
{
    public static Started Start(string exe, List<string> args, bool appId)
    {
        if (!File.Exists(exe))
        {
            throw new FileNotFoundException($"the player is missing: {exe}");
        }
        if (OperatingSystem.IsWindows())
        {
            return Win.StartProcess(exe, args, appId);
        }
        throw new PlatformNotSupportedException("this system execs the player instead");
    }

    public static int RunConsole(string exe, List<string> args)
    {
        if (!File.Exists(exe))
        {
            throw new FileNotFoundException($"the player is missing: {exe}");
        }
        if (OperatingSystem.IsWindows())
        {
            return Win.RunAndWait(exe, args);
        }
        throw new PlatformNotSupportedException("the console build is Windows-only: on Linux the launcher becomes mpv itself");
    }

    public static void BringToFront(long windowId)
    {
        if (OperatingSystem.IsWindows())
        {
            Win.BringToFront((nint)windowId);
        }
    }

    public static void TagWindow(long windowId)
    {
        if (OperatingSystem.IsWindows())
        {
            Win.SetRelaunchProperties((nint)windowId);
        }
    }

    public static int OpenDialog(string[] args)
    {
        if (OperatingSystem.IsWindows())
        {
            return WinDialog.Run(args);
        }
        if (OperatingSystem.IsLinux())
        {
            return LinuxDialog.Run(args);
        }
        throw new PlatformNotSupportedException("--open-dialog is not supported on this system");
    }

    public static int Register(string[] args, bool register)
    {
        if (OperatingSystem.IsWindows())
        {
            return WinRegistration.Run(args, register);
        }
        if (OperatingSystem.IsLinux())
        {
            return LinuxDesktop.Run(args, register);
        }
        throw new PlatformNotSupportedException("--register is not supported on this system");
    }

    public static int MigrateShell(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("--migrate-shell is Windows-only");
        }
        int i = Array.IndexOf(args, "--from");
        if (i < 0 || i + 1 >= args.Length)
        {
            throw new ArgumentException("usage: --migrate-shell --from <old exe>");
        }
        return WinShellMigration.Run(Path.GetFullPath(args[i + 1]));
    }

    public static int WindowRect(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            // mpv has no window-position property, and Wayland gives no client its position.
            throw new PlatformNotSupportedException("--window-rect is Windows-only (no window position on Linux)");
        }
        if (args.Length < 1 || !long.TryParse(args[0], out long wid))
        {
            throw new ArgumentException("usage: --window-rect <window-id>");
        }
        var r = Win.GetRect((nint)wid) ?? throw new ArgumentException($"no window {wid}");
        Console.Out.WriteLine($"{r.X} {r.Y} {r.W} {r.H}");
        return 0;
    }

    /// Test aid: the taskbar properties of a window, as another process sees them.
    public static int GetWindowAppId(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows-only");
        }
        if (args.Length < 1 || !long.TryParse(args[0], out long wid))
        {
            throw new ArgumentException("usage: --get-window-appid <window-id>");
        }
        foreach (var (key, value) in Win.GetRelaunchProperties((nint)wid))
        {
            Console.Out.WriteLine($"{key}={value}");
        }
        return 0;
    }

    public static void ShowError(string text)
    {
        if (OperatingSystem.IsWindows())
        {
            Win.MessageBox(text);
        }
    }
}
