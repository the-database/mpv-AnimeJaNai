namespace AnimeJaNai.Launcher;

/// A started player process.
sealed record Started(int ExitCode, Func<bool> IsAlive);

/// Operating-system specific parts. Windows lives in Windows/*.cs; Linux follows in Phase 2.
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
        throw new PlatformNotSupportedException("starting the player is Windows-only so far");
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
        throw new PlatformNotSupportedException("the console launcher is Windows-only so far");
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
        throw new PlatformNotSupportedException("--open-dialog is Windows-only so far");
    }

    public static int Register(string[] args, bool register)
    {
        if (OperatingSystem.IsWindows())
        {
            return WinRegistration.Run(args, register);
        }
        throw new PlatformNotSupportedException("--register is Windows-only so far");
    }

    public static int WindowRect(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("--window-rect is Windows-only");
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
