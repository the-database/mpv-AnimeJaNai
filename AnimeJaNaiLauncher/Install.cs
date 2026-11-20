using System.Security.Cryptography;
using System.Text;

namespace AnimeJaNai.Launcher;

/// Where things are, relative to the launcher, and the names derived from them.
static class Install
{
    /// Taskbar / Start menu identity (Windows AppUserModelID). Shortcuts the installer makes carry
    /// the same id, so a pinned shortcut and the running player share one taskbar button.
    public const string AppId = "TheDatabase.mpvAnimeJaNai";
    public const string DisplayName = "mpv-AnimeJaNai";

    /// The folder the launcher is in: the install root.
    public static string Root { get; } = AppContext.BaseDirectory.TrimEnd('\\', '/');

    /// This launcher (the GUI build on Windows, whichever build is running elsewhere).
    public static string LauncherPath { get; } = Environment.ProcessPath ?? System.IO.Path.Combine(Root, "mpv-animejanai.exe");

    /// The GUI launcher's path, for relaunch commands and associations, even when the console
    /// build is the one running.
    public static string GuiLauncherPath =>
        OperatingSystem.IsWindows() ? System.IO.Path.Combine(Root, "mpv-animejanai.exe") : LauncherPath;

    public static string ConfigDir { get; } =
        Environment.GetEnvironmentVariable("ANIMEJANAI_CONFIG_DIR") is { Length: > 0 } dir
            ? System.IO.Path.GetFullPath(dir)
            : System.IO.Path.Combine(Root, "portable_config");

    /// The interface: one mpv script directory (script name animejanai_ui), loaded only by the launcher.
    public static string UiDir => System.IO.Path.Combine(ConfigDir, "animejanai-ui");

    public static string Player(bool console) =>
        OperatingSystem.IsWindows()
            ? System.IO.Path.Combine(Root, console ? "mpv.com" : "mpv.exe")
            : System.IO.Path.Combine(Root, "mpv", "mpv");

    /// mpv-animejanai-<first 8 hex of SHA-256 of the lower-cased config dir>: one per config
    /// (not per install folder: the AppImage's install folder is a new mount point every run).
    public static string InstanceName { get; } = "mpv-animejanai-" + Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(ConfigDir.ToLowerInvariant())))[..8];

    /// The --input-ipc-server value for an instance name.
    public static string IpcServer(string name) =>
        OperatingSystem.IsWindows()
            ? $@"\\.\pipe\{name}"
            : System.IO.Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } run ? run : "/tmp", name + ".sock");
}
