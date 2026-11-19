// mpv-animejanai: starts the bundled mpv with the AnimeJaNai config and interface, or hands files
// to the one already running. Also the native helper the interface scripts call (file dialogs,
// file associations, window position).
//
//   mpv-animejanai [files] [mpv options]   open in the running player, or start one
//   mpv-animejanai --no-ui [...]            the same player and config, without the interface,
//                                           single instance or taskbar identity (plain mpv)
//   mpv-animejanai --new-instance [...]     always start a new player
//   mpv-animejanai --open-dialog --owner <window-id> [--multi] [--folder] [--title T]
//                  [--filter "Name|*.a;*.b|Name|*.*"] [--initial <dir>]
//                                           native file picker; UTF-8 paths on stdout, one per
//                                           line; exit 1 when cancelled (Linux: 3 when there is
//                                           no dialog program, zenity or kdialog)
//   mpv-animejanai --register video|audio|image|path [--default]
//   mpv-animejanai --unregister video|audio|image|path|all
//                                           per-user file associations and PATH (Windows)
//   mpv-animejanai --register desktop [--default] [--exec <path>]
//   mpv-animejanai --unregister desktop|all  menu entry, icon, default player (Linux)
//   mpv-animejanai --window-rect <window-id> prints "x y w h" as --geometry takes them (Windows)
//
// Any other argument that starts with "-" is an mpv option. A launch with mpv options starts its
// own player, like --new-instance. The console build (shipped as mpv-animejanai.com) runs mpv.com
// in the caller's console and waits for it. On Linux there is one build: it execs mpv/mpv with
// the bundled libraries on LD_LIBRARY_PATH, so the launcher process becomes the player.
//
// Environment: ANIMEJANAI_CONFIG_DIR replaces <install>/portable_config (tests, the AppImage);
// ANIMEJANAI_LAUNCHER_LOG=<file> appends a timing log; on Linux ANIMEJANAI_EXTRA_LIBS adds
// library folders after the bundled ones, and the player gets the caller's LD_LIBRARY_PATH in
// ANIMEJANAI_ORIG_LD_LIBRARY_PATH (for the programs it runs).
using AnimeJaNai.Launcher;

return Launcher.Run(args);

namespace AnimeJaNai.Launcher
{
    static class Launcher
    {
        public static int Run(string[] args)
        {
            try
            {
                if (args.Length > 0)
                {
                    switch (args[0])
                    {
                        case "--open-dialog":
                            return Platform.OpenDialog(args[1..]);
                        case "--register":
                            return Platform.Register(args[1..], register: true);
                        case "--unregister":
                            return Platform.Register(args[1..], register: false);
                        case "--window-rect":
                            return Platform.WindowRect(args[1..]);
                        case "--get-window-appid":
                            return Platform.GetWindowAppId(args[1..]);
                        case "--help":
                        case "-h":
                            Out.Info(Usage);
                            return 0;
                    }
                }
                return Launch.Run(args);
            }
            catch (Exception e)
            {
                Out.Error($"mpv-animejanai: {e.Message}");
                Log.Write(e.ToString());
                return 2;
            }
        }

        const string Usage =
            "usage: mpv-animejanai [files] [mpv options]\n" +
            "       mpv-animejanai --no-ui | --new-instance [files] [mpv options]\n" +
            "       mpv-animejanai --open-dialog --owner <window-id> [--multi] [--folder] [--title T] [--filter F] [--initial DIR]\n" +
            "       mpv-animejanai --register video|audio|image|path [--default]\n" +
            "       mpv-animejanai --unregister video|audio|image|path|all\n" +
            "       mpv-animejanai --register desktop [--default] [--exec PATH]   (Linux)\n" +
            "       mpv-animejanai --unregister desktop|all                      (Linux)\n" +
            "       mpv-animejanai --window-rect <window-id>";
    }

    /// Messages for the user: stderr in the console build, a message box in the GUI build
    /// (which has no console) for errors.
    static class Out
    {
        public static void Info(string text) => Console.Out.WriteLine(text);

        public static void Error(string text)
        {
            Console.Error.WriteLine(text);
#if !CONSOLE_BUILD
            // Started from Explorer, a pin or a shortcut there is nowhere for stderr to go.
            if (OperatingSystem.IsWindows() && !Win.HasStdErr())
            {
                Platform.ShowError(text);
            }
#endif
        }
    }

    static class Log
    {
        static readonly string? Path = Environment.GetEnvironmentVariable("ANIMEJANAI_LAUNCHER_LOG") is { Length: > 0 } p ? p : null;
        static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

        public static void Write(string text)
        {
            if (Path is null)
            {
                return;
            }
            try
            {
                File.AppendAllText(Path, $"[{Environment.ProcessId} {Clock.Elapsed.TotalMilliseconds,8:F1} ms] {text}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
        }
    }
}
