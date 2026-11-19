using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace AnimeJaNai.Launcher;

/// --migrate-shell --from <old exe>: what pointed at the old player exe (mpvnet.exe) points at this
/// launcher afterwards. Only our own entries, and only those naming exactly that exe:
///  - the AnimeJaNai.mpv ProgID the installer registered (open command, icon);
///  - .lnk files in the user's Start menu, desktop and taskbar/Start pins: new target, the
///    launcher's AppUserModelID (so a pinned shortcut and the running player share a button).
/// mpv.net's own registrations (machine-wide, under its names) are not ours to change. Explorer may
/// keep showing a rewritten pin's old icon until it restarts.
/// Tests: ANIMEJANAI_TEST_HKCU redirects the registry (as for --register), and
/// ANIMEJANAI_TEST_SHELL_DIRS=<dir>;<dir> replaces the folders searched for shortcuts.
[SupportedOSPlatform("windows")]
static class WinShellMigration
{
    static readonly Guid CLSID_ShellLink = new("00021401-0000-0000-C000-000000000046");
    static readonly Guid AppUserModelFmtId = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    public static int Run(string oldExe)
    {
        bool test = WinRegistration.RedirectForTests();
        int changed = RepointProgId(oldExe);
        foreach (var dir in ShortcutFolders())
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }
            foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories))
            {
                try
                {
                    if (RepointShortcut(lnk, oldExe))
                    {
                        Console.Out.WriteLine($"re-pointed {lnk}");
                        changed++;
                    }
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"skipped {lnk}: {e.Message}");
                }
            }
        }
        if (changed > 0 && !test)
        {
            SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, 0, 0);
        }
        Console.Out.WriteLine($"{changed} shortcut(s)/association(s) moved from {Path.GetFileName(oldExe)} to {Path.GetFileName(Install.GuiLauncherPath)}");
        return 0;
    }

    static IEnumerable<string> ShortcutFolders()
    {
        if (Environment.GetEnvironmentVariable("ANIMEJANAI_TEST_SHELL_DIRS") is { Length: > 0 } dirs)
        {
            return dirs.Split(';', StringSplitOptions.RemoveEmptyEntries);
        }
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.Combine(appData, @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"),
            Path.Combine(appData, @"Microsoft\Internet Explorer\Quick Launch\User Pinned\StartMenu"),
        ];
    }

    static bool Names(string? value, string exe) =>
        value is not null && value.Contains(exe, StringComparison.OrdinalIgnoreCase);

    static int RepointProgId(string oldExe)
    {
        int changed = 0;
        using var prog = Registry.CurrentUser.OpenSubKey(@"Software\Classes\AnimeJaNai.mpv", writable: true);
        if (prog is null)
        {
            return 0;
        }
        using (var command = prog.OpenSubKey(@"shell\open\command", writable: true))
        {
            if (Names(command?.GetValue("") as string, oldExe))
            {
                command!.SetValue("", WinRegistration.OpenCommand);
                changed++;
            }
        }
        using (var icon = prog.OpenSubKey("DefaultIcon", writable: true))
        {
            if (Names(icon?.GetValue("") as string, oldExe))
            {
                icon!.SetValue("", WinRegistration.Icon);
            }
        }
        return changed;
    }

    static bool RepointShortcut(string lnk, string oldExe)
    {
        var cw = new StrategyBasedComWrappers();
        var clsid = CLSID_ShellLink;
        var iid = typeof(IShellLinkW).GUID;
        int hr = CoCreateInstance(ref clsid, 0, 1 /* CLSCTX_INPROC_SERVER */, ref iid, out nint ptr);
        if (hr != 0)
        {
            throw new IOException($"ShellLink 0x{hr:X8}");
        }
        var link = (IShellLinkW)cw.GetOrCreateObjectForComInstance(ptr, CreateObjectFlags.None);
        Marshal.Release(ptr);
        var file = (IPersistFile)link;
        Check(file.Load(lnk, 2 /* STGM_READWRITE */));

        string target = GetString((buf, n) => link.GetPath(buf, n, 0, 4 /* SLGP_RAWPATH */));
        if (!string.Equals(Path.GetFullPath(Environment.ExpandEnvironmentVariables(target)), oldExe, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        Check(link.SetPath(Install.GuiLauncherPath));
        int iconIndex = 0;
        string icon = GetString((buf, n) => link.GetIconLocation(buf, n, out iconIndex));
        if (icon.Length == 0 || Names(icon, oldExe))
        {
            Check(link.SetIconLocation(Path.Combine(Install.Root, "mpv.exe"), 0));
        }
        var store = (IPropertyStore)link;
        var key = new PROPERTYKEY { fmtid = AppUserModelFmtId, pid = 5 };
        var pv = new PROPVARIANT { vt = 31 /* VT_LPWSTR */, ptr = Marshal.StringToCoTaskMemUni(Install.AppId) };
        try
        {
            Check(store.SetValue(ref key, ref pv));
            Check(store.Commit());
        }
        finally
        {
            Marshal.FreeCoTaskMem(pv.ptr);
        }
        Check(file.Save(null, true));
        return true;
    }

    static string GetString(Func<nint, int, int> get)
    {
        nint buf = Marshal.AllocHGlobal(32768 * 2);
        try
        {
            Marshal.WriteInt16(buf, 0);
            int hr = get(buf, 32768);
            return hr < 0 ? "" : Marshal.PtrToStringUni(buf) ?? "";
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    static void Check(int hr)
    {
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    [DllImport("ole32.dll")] static extern int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, out nint ppv);
    [DllImport("shell32.dll")] static extern void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}

[GeneratedComInterface, Guid("000214F9-0000-0000-C000-000000000046")]
partial interface IShellLinkW
{
    [PreserveSig] int GetPath(nint file, int size, nint findData, uint flags);
    [PreserveSig] int GetIDList(out nint pidl);
    [PreserveSig] int SetIDList(nint pidl);
    [PreserveSig] int GetDescription(nint name, int size);
    [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int GetWorkingDirectory(nint dir, int size);
    [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
    [PreserveSig] int GetArguments(nint args, int size);
    [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
    [PreserveSig] int GetHotkey(out ushort hotkey);
    [PreserveSig] int SetHotkey(ushort hotkey);
    [PreserveSig] int GetShowCmd(out int show);
    [PreserveSig] int SetShowCmd(int show);
    [PreserveSig] int GetIconLocation(nint path, int size, out int index);
    [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
    [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
    [PreserveSig] int Resolve(nint hwnd, uint flags);
    [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
}

[GeneratedComInterface, Guid("0000010b-0000-0000-C000-000000000046")]
partial interface IPersistFile
{
    [PreserveSig] int GetClassID(out Guid clsid);
    [PreserveSig] int IsDirty();
    [PreserveSig] int Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
    [PreserveSig] int Save([MarshalAs(UnmanagedType.LPWStr)] string? file, [MarshalAs(UnmanagedType.Bool)] bool remember);
    [PreserveSig] int SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
    [PreserveSig] int GetCurFile(out nint file);
}
