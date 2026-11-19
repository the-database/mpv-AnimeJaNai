using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace AnimeJaNai.Launcher;

/// --register / --unregister: per-user (HKCU) file associations and PATH. Never machine-wide, never
/// elevated. Windows 10+ keeps the effective default app behind a UserChoice hash nobody but the
/// user can set, so --default only sets the per-user fallback; "Open with" and the default-apps
/// page list the player either way.
[SupportedOSPlatform("windows")]
static partial class WinRegistration
{
    sealed record Kind(string Name, string ProgId, string Description, string[] Extensions);

    // The fork's default video-exts / audio-exts / image-exts, plus the two video extensions the
    // installer associated before (mts, m2v).
    static readonly Kind[] Kinds =
    [
        new("video", "AnimeJaNai.mpv", "Video",
            ["3g2", "3gp", "avi", "flv", "ivf", "m2ts", "m2v", "m4v", "mj2", "mkv", "mov", "mp4", "mpeg", "mpg",
             "mts", "mxf", "ogv", "rmvb", "ts", "webm", "wmv", "y4m"]),
        new("audio", "AnimeJaNai.mpv.audio", "Audio",
            ["aac", "ac3", "aiff", "ape", "au", "dff", "dsf", "dts", "eac3", "flac", "m4a", "mka", "mp1", "mp2",
             "mp3", "mpc", "oga", "ogg", "ogm", "opus", "tak", "thd", "tta", "wav", "wma", "wv"]),
        new("image", "AnimeJaNai.mpv.image", "Image",
            ["avif", "bmp", "gif", "heic", "heif", "j2k", "jp2", "jpeg", "jpg", "jxl", "png", "qoi", "svg", "tga",
             "tif", "tiff", "webp"]),
    ];

    const string Classes = @"Software\Classes";
    static string ApplicationKey => $@"{Classes}\Applications\{Path.GetFileName(Install.GuiLauncherPath)}";

    public static int Run(string[] args, bool register)
    {
        var what = args.Where(a => !a.StartsWith("--")).ToList();
        bool makeDefault = args.Contains("--default");
        if (what.Count == 0)
        {
            throw new ArgumentException($"usage: --{(register ? "" : "un")}register video|audio|image|path{(register ? " [--default]" : "|all")}");
        }
        bool testRoot = RedirectForTests();
        foreach (var name in what)
        {
            if (name == "path")
            {
                SetPath(register);
                if (!testRoot)
                {
                    BroadcastEnvironmentChange();
                }
                continue;
            }
            if (name == "all" && !register)
            {
                foreach (var k in Kinds)
                {
                    Unregister(k);
                }
                Registry.CurrentUser.DeleteSubKeyTree(ApplicationKey, throwOnMissingSubKey: false);
                SetPath(false);
                if (!testRoot)
                {
                    BroadcastEnvironmentChange();
                }
                continue;
            }
            var kind = Kinds.FirstOrDefault(k => k.Name == name) ?? throw new ArgumentException($"unknown kind '{name}'");
            if (register)
            {
                Register(kind, makeDefault);
            }
            else
            {
                Unregister(kind);
            }
        }
        if (!testRoot)
        {
            SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, 0, 0);
        }
        return 0;
    }

    static string OpenCommand => Win.CommandLine(Install.GuiLauncherPath, []) + " \"%1\"";
    static string Icon => Path.Combine(Install.Root, "mpv.exe") + ",0";

    static void Register(Kind kind, bool makeDefault)
    {
        var cu = Registry.CurrentUser;
        using (var prog = cu.CreateSubKey($@"{Classes}\{kind.ProgId}"))
        {
            prog.SetValue("", $"{Install.DisplayName} {kind.Description}");
            prog.SetValue("FriendlyAppName", Install.DisplayName);
            using (var icon = prog.CreateSubKey("DefaultIcon"))
            {
                icon.SetValue("", Icon);
            }
            using (var open = prog.CreateSubKey(@"shell\open"))
            {
                open.SetValue("FriendlyAppName", Install.DisplayName);
                using var command = open.CreateSubKey("command");
                command.SetValue("", OpenCommand);
            }
        }
        using (var app = cu.CreateSubKey(ApplicationKey))
        {
            app.SetValue("FriendlyAppName", Install.DisplayName);
            using (var command = app.CreateSubKey(@"shell\open\command"))
            {
                command.SetValue("", OpenCommand);
            }
            using var types = app.CreateSubKey("SupportedTypes");
            foreach (var ext in kind.Extensions)
            {
                types.SetValue("." + ext, "");
            }
        }
        foreach (var ext in kind.Extensions)
        {
            using var extKey = cu.CreateSubKey($@"{Classes}\.{ext}");
            using (var openWith = extKey.CreateSubKey("OpenWithProgids"))
            {
                openWith.SetValue(kind.ProgId, "");
            }
            if (makeDefault)
            {
                extKey.SetValue("", kind.ProgId);
            }
        }
    }

    static void Unregister(Kind kind)
    {
        var cu = Registry.CurrentUser;
        cu.DeleteSubKeyTree($@"{Classes}\{kind.ProgId}", throwOnMissingSubKey: false);
        using (var types = cu.OpenSubKey($@"{ApplicationKey}\SupportedTypes", writable: true))
        {
            foreach (var ext in kind.Extensions)
            {
                types?.DeleteValue("." + ext, throwOnMissingValue: false);
            }
        }
        foreach (var ext in kind.Extensions)
        {
            using var extKey = cu.OpenSubKey($@"{Classes}\.{ext}", writable: true);
            if (extKey is null)
            {
                continue;
            }
            using (var openWith = extKey.OpenSubKey("OpenWithProgids", writable: true))
            {
                openWith?.DeleteValue(kind.ProgId, throwOnMissingValue: false);
                if (openWith is not null && openWith.ValueCount == 0 && openWith.SubKeyCount == 0)
                {
                    extKey.DeleteSubKey("OpenWithProgids", throwOnMissingSubKey: false);
                }
            }
            if (extKey.GetValue("") as string == kind.ProgId)
            {
                extKey.DeleteValue("", throwOnMissingValue: false);
            }
            bool empty = extKey.ValueCount == 0 && extKey.SubKeyCount == 0;
            extKey.Dispose();
            if (empty)
            {
                cu.DeleteSubKey($@"{Classes}\.{ext}", throwOnMissingSubKey: false);
            }
        }
        // The Applications entry goes once no kind is registered any more.
        if (Kinds.All(k => cu.OpenSubKey($@"{Classes}\{k.ProgId}") is null))
        {
            cu.DeleteSubKeyTree(ApplicationKey, throwOnMissingSubKey: false);
        }
    }

    /// Adds or removes the install folder in the user's PATH, keeping the value's other entries
    /// and its unexpanded %VARIABLES%.
    static void SetPath(bool add)
    {
        using var env = Registry.CurrentUser.CreateSubKey("Environment");
        string current = env.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        var kind = env.GetValueNames().Contains("Path", StringComparer.OrdinalIgnoreCase) ? env.GetValueKind("Path") : RegistryValueKind.ExpandString;
        var parts = current.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        bool Same(string p) => string.Equals(p.Trim().TrimEnd('\\'), Install.Root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        bool has = parts.Any(Same);
        if (add == has)
        {
            return;
        }
        if (add)
        {
            parts.Add(Install.Root);
        }
        else
        {
            parts.RemoveAll(p => Same(p));
        }
        if (parts.Count == 0)
        {
            env.DeleteValue("Path", throwOnMissingValue: false);
            return;
        }
        env.SetValue("Path", string.Join(';', parts), kind == RegistryValueKind.String ? RegistryValueKind.String : RegistryValueKind.ExpandString);
    }

    /// Tests only: ANIMEJANAI_TEST_HKCU=<subkey> redirects HKEY_CURRENT_USER for this process to
    /// HKCU\<subkey> (RegOverridePredefKey), so a test never touches the user's real entries.
    static RegistryKey? testRoot;

    static bool RedirectForTests()
    {
        if (Environment.GetEnvironmentVariable("ANIMEJANAI_TEST_HKCU") is not { Length: > 0 } sub)
        {
            return false;
        }
        // Kept open for the life of the process: HKEY_CURRENT_USER now refers to it.
        testRoot = Registry.CurrentUser.CreateSubKey(sub);
        // HKEY_CURRENT_USER is ((HKEY)(LONG)0x80000001): sign-extended on 64-bit.
        int rc = RegOverridePredefKey((nint)unchecked((int)0x80000001), testRoot.Handle.DangerousGetHandle());
        if (rc != 0)
        {
            throw new IOException($"RegOverridePredefKey failed ({rc})");
        }
        return true;
    }

    static void BroadcastEnvironmentChange() =>
        SendMessageTimeoutW(0xFFFF /* HWND_BROADCAST */, 0x001A /* WM_SETTINGCHANGE */, 0, "Environment", 0x0002 /* SMTO_ABORTIFHUNG */, 3000, out _);

    [LibraryImport("shell32.dll")] private static partial void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
    [LibraryImport("advapi32.dll")] private static partial int RegOverridePredefKey(nint key, nint newKey);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SendMessageTimeoutW(nint hwnd, uint msg, nint wParam, string lParam, uint flags, uint timeout, out nint result);
}
