using System.Runtime.InteropServices;
using Microsoft.Win32;

// --migrate-shell: our shortcuts and ProgID move from mpvnet.exe to the launcher; nothing else does.
// Shortcuts live in a scratch folder (ANIMEJANAI_TEST_SHELL_DIRS), the registry is redirected
// (ANIMEJANAI_TEST_HKCU): the user's real ones are never touched.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
sealed partial class WindowsTests
{
    void MigrateShell()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ajn-shell-test-" + Guid.NewGuid().ToString("N")[..8]);
        string pins = Path.Combine(dir, "pins");
        Directory.CreateDirectory(Path.Combine(dir, "Programs", "mpv-AnimeJaNai"));
        Directory.CreateDirectory(pins);
        string oldExe = Path.Combine(tree, "mpvnet.exe");
        string ours = Path.Combine(dir, "Programs", "mpv-AnimeJaNai", "mpv-AnimeJaNai.lnk");
        string pinned = Path.Combine(pins, "mpv-AnimeJaNai.lnk");
        string other = Path.Combine(dir, "Programs", "Other.lnk");
        MakeShortcut(ours, oldExe);
        MakeShortcut(pinned, oldExe);
        MakeShortcut(other, Path.Combine(tree, "AnimeJaNaiManager.exe"));

        string sub = @"Software\AnimeJaNaiLauncherTest-" + Guid.NewGuid().ToString("N")[..8];
        using (var key = Registry.CurrentUser.CreateSubKey(sub + @"\Software\Classes\AnimeJaNai.mpv\shell\open\command"))
        {
            key.SetValue("", $"\"{oldExe}\" \"%1\"");
        }
        string? realBefore = Registry.CurrentUser.OpenSubKey(@"Software\Classes\AnimeJaNai.mpv\shell\open\command")?.GetValue("") as string;
        try
        {
            var env = new Dictionary<string, string>
            {
                ["ANIMEJANAI_TEST_HKCU"] = sub,
                ["ANIMEJANAI_TEST_SHELL_DIRS"] = Path.Combine(dir, "Programs") + ";" + pins,
            };
            var r = RunLauncher(gui, ["--migrate-shell", "--from", oldExe], env);
            Check(r.ExitCode == 0, $"--migrate-shell exit 0 ({r.StdOut.ReplaceLineEndings(" | ").Trim()})");
            Check(string.Equals(ShortcutTarget(ours), gui, StringComparison.OrdinalIgnoreCase), $"Start menu shortcut now opens the launcher ({ShortcutTarget(ours)})");
            Check(string.Equals(ShortcutTarget(pinned), gui, StringComparison.OrdinalIgnoreCase), "pinned shortcut now opens the launcher");
            Check(ShortcutAppId(ours) == "TheDatabase.mpvAnimeJaNai", $"shortcut carries the launcher's app id ({ShortcutAppId(ours)})");
            Check(ShortcutTarget(other).EndsWith("AnimeJaNaiManager.exe", StringComparison.OrdinalIgnoreCase), "a shortcut to something else is left alone");
            string? cmd = Registry.CurrentUser.OpenSubKey(sub + @"\Software\Classes\AnimeJaNai.mpv\shell\open\command")?.GetValue("") as string;
            Check(cmd is not null && cmd.Contains("mpv-animejanai.exe") && !cmd.Contains("mpvnet.exe"), $"AnimeJaNai.mpv opens the launcher ({cmd})");
            r = RunLauncher(gui, ["--migrate-shell", "--from", oldExe], env);
            Check(r.ExitCode == 0 && r.StdOut.Contains("0 shortcut(s)"), "a second run changes nothing");
            Check((Registry.CurrentUser.OpenSubKey(@"Software\Classes\AnimeJaNai.mpv\shell\open\command")?.GetValue("") as string) == realBefore, "the real HKCU association is untouched");
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    static void MakeShortcut(string path, string target)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic lnk = shell.CreateShortcut(path);
        lnk.TargetPath = target;
        lnk.IconLocation = target + ",0";
        lnk.Save();
    }

    static string ShortcutTarget(string path)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        return (string)shell.CreateShortcut(path).TargetPath;
    }

    static string? ShortcutAppId(string path)
    {
        var iid = typeof(IPropertyStoreClassic).GUID;
        if (SHGetPropertyStoreFromParsingName(path, 0, 0, ref iid, out var store) != 0)
        {
            return null;
        }
        var key = new PropKey { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
        store.GetValue(ref key, out var pv);
        string? id = pv.vt == 31 ? Marshal.PtrToStringUni(pv.ptr) : null;
        Marshal.ReleaseComObject(store);
        return id;
    }

    [StructLayout(LayoutKind.Sequential)] struct PropKey { public Guid fmtid; public uint pid; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] struct PropVar { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public nint ptr; }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStoreClassic
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropKey key);
        [PreserveSig] int GetValue(ref PropKey key, out PropVar value);
        [PreserveSig] int SetValue(ref PropKey key, ref PropVar value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetPropertyStoreFromParsingName(string path, nint bindContext, int flags, ref Guid iid, out IPropertyStoreClassic store);
}
