using System.Runtime.Versioning;
using System.Text;

namespace AnimeJaNai.Launcher;

/// --open-dialog on Linux: kdialog or zenity (kdialog first on KDE), run with the caller's library
/// path. Prints each chosen path as UTF-8 on stdout; exit 1 when cancelled, 3 when neither program
/// is installed (the interface then says what to install).
// TODO: xdg-desktop-portal FileChooser over the session bus first (Tmds.DBus.Protocol is Native
// AOT clean): sandboxed and portal-only desktops, and a parent window on Wayland.
[SupportedOSPlatform("linux")]
static class LinuxDialog
{
    public const int NoDialogProgram = 3;

    sealed record Options(long Owner, bool Multi, bool Folder, string? Title, List<(string Name, string[] Patterns)> Filters, string? Initial);

    public static int Run(string[] args)
    {
        var o = Parse(args);
        bool kde = (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "")
            .Split(':').Any(d => d.Equals("KDE", StringComparison.OrdinalIgnoreCase));
        string[] order = kde ? ["kdialog", "zenity"] : ["zenity", "kdialog"];
        foreach (var name in order)
        {
            if (LinuxEnv.FindProgram(name) is not string exe)
            {
                continue;
            }
            var argv = name == "kdialog" ? KDialogArgs(o) : ZenityArgs(o);
            Log.Write($"dialog: {exe}");
            var (rc, output) = LinuxEnv.RunHelper(exe, argv, captureOutput: true);
            Log.Write($"dialog: {name} exit {rc}");
            if (rc == 1)
            {
                return 1; // cancelled
            }
            if (rc != 0)
            {
                throw new IOException($"{name} failed (exit {rc})");
            }
            var paths = output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
            if (paths.Count == 0)
            {
                return 1;
            }
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { NewLine = "\n" };
            foreach (var p in paths)
            {
                stdout.WriteLine(p);
            }
            stdout.Flush();
            return 0;
        }
        Console.Error.WriteLine("mpv-animejanai: no file dialog program: install zenity or kdialog");
        return NoDialogProgram;
    }

    static Options Parse(string[] args)
    {
        long owner = 0;
        bool multi = false, folder = false;
        string? title = null, initial = null;
        var filters = new List<(string, string[])>();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--owner": owner = long.Parse(Next()); break;
                case "--multi": multi = true; break;
                case "--folder": folder = true; break;
                case "--title": title = Next(); break;
                case "--initial": initial = Next(); break;
                case "--filter":
                    // "Video files|*.mkv;*.mp4|All files|*.*": name/pattern pairs, as on Windows.
                    var parts = Next().Split('|');
                    for (int k = 0; k + 1 < parts.Length; k += 2)
                    {
                        // "*.*" is Windows for "everything"; here it would skip files without a dot.
                        var patterns = parts[k + 1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                                   .Select(p => p == "*.*" ? "*" : p).ToArray();
                        if (patterns.Length > 0)
                        {
                            filters.Add((parts[k].Trim(), patterns));
                        }
                    }
                    break;
                default: throw new ArgumentException($"unknown --open-dialog option {args[i]}");
            }
        }
        return new Options(owner, multi, folder, title, filters, initial is not null && Directory.Exists(initial) ? Path.GetFullPath(initial) : null);
    }

    static List<string> ZenityArgs(Options o)
    {
        // No --attach: zenity 4 (GTK 4) dropped it, and an unknown option is an error there.
        var a = new List<string> { "--file-selection", "--separator=\n" };
        if (o.Title is not null)
        {
            a.Add($"--title={o.Title}");
        }
        if (o.Multi)
        {
            a.Add("--multiple");
        }
        if (o.Folder)
        {
            a.Add("--directory");
        }
        if (o.Initial is not null)
        {
            a.Add($"--filename={o.Initial.TrimEnd('/')}/"); // a trailing slash opens the folder
        }
        if (!o.Folder)
        {
            foreach (var (name, patterns) in o.Filters)
            {
                a.Add($"--file-filter={name} | {string.Join(' ', patterns)}");
            }
        }
        return a;
    }

    static List<string> KDialogArgs(Options o)
    {
        var a = new List<string>();
        if (o.Title is not null)
        {
            a.AddRange(["--title", o.Title]);
        }
        // Parent on X11 (the player's window-id is an X11 window there); Wayland has no handle.
        if (o.Owner != 0 && Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is null or "")
        {
            a.AddRange(["--attach", o.Owner.ToString()]);
        }
        // ":mpv-animejanai" makes kdialog remember the last folder under that label.
        string start = o.Initial ?? ":mpv-animejanai";
        if (o.Folder)
        {
            a.AddRange(["--getexistingdirectory", start]);
            return a;
        }
        a.AddRange(["--getopenfilename", start]);
        // KDE filter syntax: "patterns|description" entries, one per line.
        a.Add(string.Join('\n', o.Filters.Select(f => $"{string.Join(' ', f.Patterns)}|{f.Name}")));
        if (o.Multi)
        {
            a.AddRange(["--multiple", "--separate-output"]);
        }
        return a;
    }
}
