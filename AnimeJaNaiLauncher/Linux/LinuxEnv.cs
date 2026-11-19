using System.Collections;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace AnimeJaNai.Launcher;

/// The player's library path, and the caller's original for the programs it runs.
[SupportedOSPlatform("linux")]
static class LinuxEnv
{
    const string LibPath = "LD_LIBRARY_PATH";
    /// The caller's LD_LIBRARY_PATH before the launcher added the bundled folders ("" when it had
    /// none). Helper programs (file dialogs, xdg-open, update-desktop-database) run with it
    /// restored, or the bundled libraries break them.
    public const string OrigLibPath = "ANIMEJANAI_ORIG_LD_LIBRARY_PATH";
    /// Extra library folders (colon-separated) after the bundled ones: the AppImage's writable
    /// inference folder and usr/lib.
    const string ExtraLibs = "ANIMEJANAI_EXTRA_LIBS";

    /// This process's environment with LD_LIBRARY_PATH =
    /// <root>/mpv:<root>/animejanai/inference[:ANIMEJANAI_EXTRA_LIBS][:the caller's value].
    public static Dictionary<string, string> PlayerEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            env[(string)e.Key] = (string?)e.Value ?? "";
        }
        // Started from inside the player (the interface runs the launcher), LD_LIBRARY_PATH already
        // has the bundled folders: build on the original instead of stacking them again.
        string caller = env.TryGetValue(OrigLibPath, out var orig) ? orig
                      : env.TryGetValue(LibPath, out var cur) ? cur : "";
        var parts = new List<string> { Path.Combine(Install.Root, "mpv"), Path.Combine(Install.Root, "animejanai", "inference") };
        foreach (var list in new[] { env.GetValueOrDefault(ExtraLibs, ""), caller })
        {
            parts.AddRange(list.Split(':', StringSplitOptions.RemoveEmptyEntries));
        }
        env[LibPath] = string.Join(':', parts);
        env[OrigLibPath] = caller;
        return env;
    }

    /// Gives a helper program the caller's library path back.
    public static void RestoreForHelper(ProcessStartInfo psi)
    {
        if (Environment.GetEnvironmentVariable(OrigLibPath) is not string orig)
        {
            return;
        }
        if (orig.Length == 0)
        {
            psi.Environment.Remove(LibPath);
        }
        else
        {
            psi.Environment[LibPath] = orig;
        }
        psi.Environment.Remove(OrigLibPath);
    }

    /// A program on PATH, or null.
    public static string? FindProgram(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            string p = Path.Combine(dir, name);
            try
            {
                if (File.Exists(p) && (File.GetUnixFileMode(p) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                {
                    return p;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return null;
    }

    /// Runs a helper with the caller's library path; stdout captured as UTF-8 when asked.
    public static (int ExitCode, string StdOut) RunHelper(string exe, IEnumerable<string> args, bool captureOutput)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = captureOutput,
            StandardOutputEncoding = captureOutput ? new System.Text.UTF8Encoding(false) : null,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        RestoreForHelper(psi);
        using var p = Process.Start(psi) ?? throw new IOException($"could not start {exe}");
        string output = captureOutput ? p.StandardOutput.ReadToEnd() : "";
        p.WaitForExit();
        return (p.ExitCode, output);
    }
}
