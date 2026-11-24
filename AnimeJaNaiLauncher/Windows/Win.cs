using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using System.Text;

namespace AnimeJaNai.Launcher;

/// Windows: process start with a taskbar identity, window properties, focus.
[SupportedOSPlatform("windows")]
static partial class Win
{
    const int STARTF_TITLEISAPPID = 0x00001000;

    /// Starts the player detached, with Install.AppId as its AppUserModelID when `appId`
    /// (STARTF_TITLEISAPPID: the id travels in lpTitle, the way the shell starts shortcuts).
    public static Started StartProcess(string exe, List<string> args, bool appId)
    {
        var si = new STARTUPINFOW { cb = Marshal.SizeOf<STARTUPINFOW>() };
        nint title = 0;
        if (appId)
        {
            title = Marshal.StringToHGlobalUni(Install.AppId);
            si.lpTitle = title;
            si.dwFlags = STARTF_TITLEISAPPID;
        }
        try
        {
            var cmd = new StringBuilder(CommandLine(exe, args));
            if (!CreateProcessW(exe, cmd, 0, 0, false, 0, 0, null, ref si, out var pi))
            {
                int err = Marshal.GetLastWin32Error();
                throw new IOException($"could not start {exe} (error {err})");
            }
            CloseHandle(pi.hThread);
            nint process = pi.hProcess;
            Log.Write($"started {Path.GetFileName(exe)} pid {pi.dwProcessId}");
            return new Started(0, () => WaitForSingleObject(process, 0) == WAIT_TIMEOUT);
        }
        finally
        {
            if (title != 0)
            {
                Marshal.FreeHGlobal(title);
            }
        }
    }

    /// Runs a console program in this console and returns its exit code.
    public static int RunAndWait(string exe, List<string> args)
    {
        var si = new STARTUPINFOW { cb = Marshal.SizeOf<STARTUPINFOW>() };
        var cmd = new StringBuilder(CommandLine(exe, args));
        if (!CreateProcessW(exe, cmd, 0, 0, true, 0, 0, null, ref si, out var pi))
        {
            throw new IOException($"could not start {exe} (error {Marshal.GetLastWin32Error()})");
        }
        // Ctrl+C reaches both processes; this one only waits for mpv to finish.
        SetConsoleCtrlHandler(0, true);
        WaitForSingleObject(pi.hProcess, INFINITE);
        GetExitCodeProcess(pi.hProcess, out uint code);
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return (int)code;
    }

    /// A command line that CommandLineToArgvW (and mpv) splits back into exactly `args`.
    public static string CommandLine(string exe, IEnumerable<string> args)
    {
        var sb = new StringBuilder();
        Quote(sb, exe);
        foreach (var a in args)
        {
            sb.Append(' ');
            Quote(sb, a);
        }
        return sb.ToString();
    }

    static void Quote(StringBuilder sb, string a)
    {
        if (a.Length > 0 && a.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            sb.Append(a);
            return;
        }
        sb.Append('"');
        int backslashes = 0;
        foreach (char ch in a)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }
            if (ch == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(ch);
            }
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
    }

    /// Restores a minimized player and brings it to the front. Works when this launcher was
    /// started by a user action (Explorer, a pin), which gives it the right to change focus.
    public static void BringToFront(nint hwnd)
    {
        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, SW_RESTORE);
        }
        bool ok = SetForegroundWindow(hwnd);
        Log.Write($"foreground {ok}");
    }

    public static (int X, int Y, int W, int H)? GetRect(nint hwnd)
    {
        if (!IsWindow(hwnd) || !GetWindowRect(hwnd, out var r))
        {
            return null;
        }
        return (r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// Whether this process has a usable stderr (a console, or a pipe/file its caller reads).
    public static bool HasStdErr()
    {
        nint h = GetStdHandle(-12 /* STD_ERROR_HANDLE */);
        return h != 0 && h != -1 && GetFileType(h) != 0 /* FILE_TYPE_UNKNOWN */;
    }

    public static void MessageBox(string text) => MessageBoxW(0, text, Install.DisplayName, 0x10 /* MB_ICONERROR */);

    // ---- window relaunch properties --------------------------------------------------------------

    static readonly Guid AppUserModelFmtId = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    const uint PidRelaunchCommand = 2, PidRelaunchIcon = 3, PidRelaunchName = 4, PidId = 5;

    /// The window's AppUserModelID and what a pin of it relaunches: the launcher, named and iconed
    /// as the player. Works on another process's window.
    public static void SetRelaunchProperties(nint hwnd)
    {
        var store = PropertyStore(hwnd);
        if (store is null)
        {
            return;
        }
        string launcher = Install.GuiLauncherPath;
        string icon = Path.Combine(Install.Root, "mpv.exe") + ",0";
        int r1 = Set(store, PidId, Install.AppId);
        int r2 = Set(store, PidRelaunchCommand, CommandLine(launcher, []));
        int r3 = Set(store, PidRelaunchName, Install.DisplayName);
        int r4 = Set(store, PidRelaunchIcon, icon);
        int r5 = store.Commit();
        Log.Write($"relaunch properties 0x{r1:X} 0x{r2:X} 0x{r3:X} 0x{r4:X} commit 0x{r5:X}");
    }

    public static List<(string, string)> GetRelaunchProperties(nint hwnd)
    {
        var result = new List<(string, string)>();
        var store = PropertyStore(hwnd);
        if (store is null)
        {
            return result;
        }
        foreach (var (name, pid) in new[] { ("id", PidId), ("command", PidRelaunchCommand), ("name", PidRelaunchName), ("icon", PidRelaunchIcon) })
        {
            var key = new PROPERTYKEY { fmtid = AppUserModelFmtId, pid = pid };
            store.GetValue(ref key, out var pv);
            string value = pv.vt == VT_LPWSTR ? Marshal.PtrToStringUni(pv.ptr) ?? "" : "";
            PropVariantClear(ref pv);
            result.Add((name, value));
        }
        return result;
    }

    static IPropertyStore? PropertyStore(nint hwnd)
    {
        var iid = typeof(IPropertyStore).GUID;
        int hr = SHGetPropertyStoreForWindow(hwnd, ref iid, out nint ptr);
        if (hr != 0)
        {
            Log.Write($"SHGetPropertyStoreForWindow 0x{hr:X8}");
            return null;
        }
        var store = (IPropertyStore)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(ptr, CreateObjectFlags.None);
        Marshal.Release(ptr);
        return store;
    }

    static int Set(IPropertyStore store, uint pid, string value)
    {
        var key = new PROPERTYKEY { fmtid = AppUserModelFmtId, pid = pid };
        var pv = new PROPVARIANT { vt = VT_LPWSTR, ptr = Marshal.StringToCoTaskMemUni(value) };
        try
        {
            return store.SetValue(ref key, ref pv);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pv.ptr);
        }
    }

    // ---- interop ---------------------------------------------------------------------------------

    const uint INFINITE = 0xFFFFFFFF, WAIT_TIMEOUT = 0x102;
    const int SW_RESTORE = 9;
    const ushort VT_LPWSTR = 31;

    [StructLayout(LayoutKind.Sequential)]
    struct STARTUPINFOW
    {
        public int cb;
        public nint lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION
    {
        public nint hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(string? app, StringBuilder cmd, nint pa, nint ta, bool inherit, uint flags,
                                      nint env, string? cwd, ref STARTUPINFOW si, out PROCESS_INFORMATION pi);

    [LibraryImport("kernel32.dll")] private static partial uint WaitForSingleObject(nint h, uint ms);
    [LibraryImport("kernel32.dll")] private static partial nint GetStdHandle(int which);
    [LibraryImport("kernel32.dll")] private static partial uint GetFileType(nint h);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetExitCodeProcess(nint h, out uint code);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CloseHandle(nint h);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetConsoleCtrlHandler(nint handler, [MarshalAs(UnmanagedType.Bool)] bool add);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetForegroundWindow(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsIconic(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsWindow(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ShowWindow(nint h, int cmd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetWindowRect(nint h, out RECT r);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int MessageBoxW(nint owner, string text, string caption, uint type);
    [LibraryImport("shell32.dll")] private static partial int SHGetPropertyStoreForWindow(nint hwnd, ref Guid iid, out nint ppv);
    [LibraryImport("ole32.dll")] private static partial int PropVariantClear(ref PROPVARIANT pv);
}
