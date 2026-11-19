using System.Runtime.Versioning;
using System.Text;

namespace AnimeJaNai.Launcher;

/// --register desktop [--default] [--exec <path>] / --unregister desktop|all: the menu entry
/// ($XDG_DATA_HOME/applications/mpv-animejanai.desktop), its icon, and with --default the default
/// player for the video types ($XDG_CONFIG_HOME/mimeapps.list). Per-user, never system-wide.
/// install.sh, uninstall.sh and the AppImage's --install / --uninstall call this, so there is one
/// writer of these files. --exec names what the entry starts (the AppImage passes its own file,
/// whose mount path is not stable); the default is this launcher.
[SupportedOSPlatform("linux")]
static class LinuxDesktop
{
    public const string DesktopId = "mpv-animejanai.desktop";
    const string IconName = "mpv-animejanai";

    static readonly string[] VideoMimeTypes =
    [
        "video/x-matroska", "video/mp4", "video/webm", "video/x-msvideo", "video/quicktime", "video/mpeg",
        "video/x-flv", "video/3gpp",
    ];

    static string Home => Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } h ? h : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static string DataHome => Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d && Path.IsPathRooted(d) ? d : Path.Combine(Home, ".local", "share");
    static string ConfigHome => Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } d && Path.IsPathRooted(d) ? d : Path.Combine(Home, ".config");
    static string AppsDir => Path.Combine(DataHome, "applications");
    static string DesktopFile => Path.Combine(AppsDir, DesktopId);
    static string IconFile => Path.Combine(DataHome, "icons", "hicolor", "256x256", "apps", IconName + ".png");
    static string MimeAppsList => Path.Combine(ConfigHome, "mimeapps.list");

    public static int Run(string[] args, bool register)
    {
        var what = new List<string>();
        bool makeDefault = false;
        string exec = Install.LauncherPath;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--default": makeDefault = true; break;
                case "--exec":
                    exec = i + 1 < args.Length ? Path.GetFullPath(args[++i]) : throw new ArgumentException("--exec needs a path");
                    break;
                default:
                    if (args[i].StartsWith("--"))
                    {
                        throw new ArgumentException($"unknown option {args[i]}");
                    }
                    what.Add(args[i]);
                    break;
            }
        }
        if (what.Count == 0 || what.Any(w => w != "desktop" && !(w == "all" && !register)))
        {
            throw new ArgumentException(register
                ? "usage: --register desktop [--default] [--exec <path>] (on Linux the menu entry covers every file kind)"
                : "usage: --unregister desktop|all");
        }
        if (register)
        {
            Register(exec, makeDefault);
        }
        else
        {
            Unregister();
        }
        UpdateDesktopDatabase();
        return 0;
    }

    static void Register(string exec, bool makeDefault)
    {
        // TODO: no player artwork yet (an open question to the user). Until it is committed as
        // installer/linux/mpv-animejanai.png and shipped at the tree root, an icon is copied only
        // when the tree has one (the AppImage generates a placeholder) and the entry otherwise
        // borrows mpv's.
        string icon = "mpv";
        foreach (var candidate in new[] { "mpv-animejanai.png", "animejanai.png", Path.Combine("portable_config", "animejanai.png") })
        {
            string src = Path.Combine(Install.Root, candidate);
            if (File.Exists(src))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(IconFile)!);
                File.Copy(src, IconFile, overwrite: true);
                icon = IconName;
                break;
            }
        }
        Directory.CreateDirectory(AppsDir);
        WriteAtomically(DesktopFile, DesktopEntry(exec, icon));
        if (makeDefault)
        {
            EditMimeApps(defaults: true);
        }
    }

    static void Unregister()
    {
        DeleteIfThere(DesktopFile);
        DeleteIfThere(IconFile);
        EditMimeApps(defaults: false);
    }

    /// The entry. Exec has the launcher, then "--" so nothing a file manager passes is read as an
    /// option; StartupWMClass matches the player's --x11-name / --wayland-app-id.
    public static string DesktopEntry(string exec, string icon) =>
        "[Desktop Entry]\n" +
        "Type=Application\n" +
        "Name=mpv AnimeJaNai\n" +
        "GenericName=Media Player\n" +
        "Comment=Real-time anime upscaling video player (mpv + TensorRT)\n" +
        $"Exec={EscapeString(ExecArgument(exec))} -- %U\n" +
        $"TryExec={EscapeString(exec)}\n" +
        $"Icon={icon}\n" +
        "Terminal=false\n" +
        "Categories=AudioVideo;Player;Video;\n" +
        $"MimeType={string.Join(';', VideoMimeTypes)};\n" +
        "StartupNotify=true\n" +
        $"StartupWMClass={Install.LinuxAppId}\n";

    /// One Exec argument, quoted by the Desktop Entry rules when it has a reserved character.
    static string ExecArgument(string a)
    {
        if (a.Contains('\n') || a.Contains('\r'))
        {
            throw new ArgumentException("the launcher path has a line break; a desktop entry cannot start it");
        }
        a = a.Replace("%", "%%");
        if (a.IndexOfAny([' ', '\t', '"', '\'', '\\', '>', '<', '~', '|', '&', ';', '$', '*', '?', '#', '(', ')', '`']) < 0)
        {
            return a;
        }
        var sb = new StringBuilder("\"");
        foreach (char c in a)
        {
            if (c is '"' or '`' or '$' or '\\')
            {
                sb.Append('\\');
            }
            sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    /// The general escaping of a desktop-entry string value (after Exec's own quoting).
    static string EscapeString(string s) => s.Replace("\\", "\\\\").Replace("\t", "\\t");

    /// Adds this entry as the first default for the video types, or takes it out of every list
    /// (defaults and added associations) and drops keys left empty. Other lines stay as they are.
    static void EditMimeApps(bool defaults)
    {
        var lines = File.Exists(MimeAppsList) ? File.ReadAllLines(MimeAppsList).ToList() : [];
        if (!defaults && lines.Count == 0)
        {
            return;
        }
        var output = new List<string>();
        var missing = defaults ? VideoMimeTypes.ToList() : [];
        string? group = null;
        foreach (var raw in lines)
        {
            string line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                group = line[1..^1];
                output.Add(raw);
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq <= 0 || line.StartsWith('#') || group is not ("Default Applications" or "Added Associations"))
            {
                output.Add(raw);
                continue;
            }
            string key = line[..eq].Trim();
            var ids = line[(eq + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                       .Where(id => id != DesktopId).ToList();
            if (group == "Default Applications" && missing.Remove(key))
            {
                ids.Insert(0, DesktopId);
            }
            if (ids.Count > 0)
            {
                output.Add($"{key}={string.Join(';', ids)};");
            }
        }
        if (missing.Count > 0)
        {
            int at = output.FindIndex(l => l.Trim() == "[Default Applications]");
            if (at < 0)
            {
                while (output.Count > 0 && output[^1].Trim().Length == 0)
                {
                    output.RemoveAt(output.Count - 1);
                }
                if (output.Count > 0)
                {
                    output.Add("");
                }
                output.Add("[Default Applications]");
                at = output.Count - 1;
            }
            output.InsertRange(at + 1, missing.Select(m => $"{m}={DesktopId};"));
        }
        Directory.CreateDirectory(ConfigHome);
        WriteAtomically(MimeAppsList, string.Join('\n', output) + "\n");
    }

    static void UpdateDesktopDatabase()
    {
        if (Directory.Exists(AppsDir) && LinuxEnv.FindProgram("update-desktop-database") is string exe)
        {
            try
            {
                LinuxEnv.RunHelper(exe, [AppsDir], captureOutput: false);
            }
            catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
            {
                Log.Write($"update-desktop-database: {e.Message}");
            }
        }
    }

    static void WriteAtomically(string path, string text)
    {
        string tmp = path + ".tmp-" + Environment.ProcessId;
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    static void DeleteIfThere(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
