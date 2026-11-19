// Scripted checks for the mpv-animejanai launcher (Windows).
//
//   dotnet run --project tests/Launcher -- <release tree> <launcher dir> [--only registry]
//
// <release tree>: an extracted mpv-upscale-2x_animejanai package (a scratch copy: the launcher is
// copied into it). <launcher dir>: the published mpv-animejanai.exe and mpv-animejanai.com.
// Players run against a scratch config dir (paused, muted, small window), on lavfi test sources,
// so nothing of the user's install or config is used. Windows open on the desktop while it runs.
// --only registry runs just the registry checks (no player, no window: fine for CI).
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32;

// Run by the player as the interface's launcher (see UiTests.cs).
if (args.Length > 0 && args[0].StartsWith("--"))
{
    return StubLauncher.Run(args);
}
if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Windows only (Linux checks come with the Linux launcher).");
    return 2;
}
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: dotnet run --project tests/Launcher -- <release tree> <launcher dir> [--only registry]");
    return 2;
}
string tree = Path.GetFullPath(args[0]);
string launcherDir = Path.GetFullPath(args[1]);
bool onlyRegistry = args.Length > 3 && args[2] == "--only" && args[3] == "registry";
var t = new Tests(tree, launcherDir);
return t.Run(onlyRegistry);

sealed partial class Tests(string tree, string launcherDir)
{
    readonly string gui = Path.Combine(tree, "mpv-animejanai.exe");
    readonly string con = Path.Combine(tree, "mpv-animejanai.com");
    readonly string config = Path.Combine(Path.GetTempPath(), "ajn-launcher-test-" + Guid.NewGuid().ToString("N")[..8], "portable_config");
    int failed, passed;

    string InstanceName => "mpv-animejanai-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(config.ToLowerInvariant())))[..8];

    public int Run(bool onlyRegistry)
    {
        File.Copy(Path.Combine(launcherDir, "mpv-animejanai.exe"), gui, overwrite: true);
        File.Copy(Path.Combine(launcherDir, "mpv-animejanai.com"), con, overwrite: true);
        WriteScratchConfig();
        try
        {
            Section("registry (redirected HKCU)", Registration);
            Section("--migrate-shell (scratch shortcuts, redirected HKCU)", MigrateShell);
            if (!onlyRegistry)
            {
                Section("first launch, window identity, interface contract", FirstLaunch);
                Section("hand-off to the running player", HandOff);
                Section("five launchers at once", Storm);
                Section("--new-instance and --no-ui", SeparateInstances);
                Section("console variant", Console_);
                Section("--open-dialog", Dialog);
                Section("interface scripts and menu", Interface);
                Section("remembered volume and window", RememberedState);
                Section("hand-off from mpv.net after the switch", HandOffFromMpvNet);
            }
        }
        finally
        {
            KillPlayers();
            try { Directory.Delete(Path.GetDirectoryName(config)!, true); } catch (IOException) { }
        }
        Console.WriteLine($"\n{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    // ---- sections --------------------------------------------------------------------------------

    void FirstLaunch()
    {
        KillPlayers();
        var sw = Stopwatch.StartNew();
        int rc = RunLauncher(gui, ["av://lavfi:testsrc=d=600"]).ExitCode;
        Check(rc == 0, $"launcher exit 0 (got {rc}), {sw.ElapsedMilliseconds} ms including window tagging");
        Check(PlayerCount() == 1, $"one player under the tree (got {PlayerCount()})");
        using var ipc = Ipc.Connect(InstanceName);
        Check(ipc is not null, "player answers on the instance pipe");
        if (ipc is null)
        {
            return;
        }
        Check(PlaylistCount(ipc) == 1, "playlist has the one file");
        long wid = WaitWindow(ipc);
        Check(wid != 0, "player has a window");
        var props = RunLauncher(gui, ["--get-window-appid", wid.ToString()]).StdOut;
        Check(props.Contains("id=TheDatabase.mpvAnimeJaNai"), "window app id set");
        Check(props.Contains($"command={gui}") || props.Contains($"command=\"{gui}\""), "relaunch command is the launcher");
        // Case-insensitive: Windows keeps these strings as global atoms, which keep the first casing
        // registered since boot.
        Check(props.Contains("name=mpv-AnimeJaNai", StringComparison.OrdinalIgnoreCase), $"relaunch display name set ({props.ReplaceLineEndings("; ")})");
        Check(props.Contains("icon=" + Path.Combine(tree, "mpv.exe") + ",0"), "relaunch icon set");
        Check(ipc.Get("user-data/test-ui/loaded")?.GetValue<bool>() == true, "interface script loaded from <config>/animejanai-ui");
        string opts = ipc.Get("script-opts")?.ToJsonString() ?? "";
        Check(opts.Contains("select-menu_conf_path") && opts.Contains("menu.conf"), "menu.conf path passed to select.lua");
        Check(opts.Contains("animejanai_ui-launcher") && opts.Contains("mpv-animejanai.exe"), "launcher path passed to the interface");
        var rect = RunLauncher(gui, ["--window-rect", wid.ToString()]).StdOut.Trim().Split(' ');
        Check(rect.Length == 4 && int.Parse(rect[2]) > 0 && int.Parse(rect[3]) > 0, $"--window-rect prints x y w h ({string.Join(' ', rect)})");
    }

    void HandOff()
    {
        Thread.Sleep(1800); // past the append window: a new launch replaces
        string realFile = Path.Combine(Path.GetDirectoryName(config)!, "テスト clip.mkv");
        var sw = Stopwatch.StartNew();
        int rc = RunLauncher(gui, ["av://lavfi:testsrc2=d=600", realFile]).ExitCode;
        Check(rc == 0, $"forwarding launcher exit 0, {sw.ElapsedMilliseconds} ms");
        Check(PlayerCount() == 1, "still one player");
        using var ipc = Ipc.Connect(InstanceName)!;
        var list = ipc.Get("playlist")!.AsArray();
        Check(list.Count == 2, $"replace + append leaves two entries (got {list.Count})");
        Check(list.Count == 2 && list[0]!["filename"]!.GetValue<string>().Contains("testsrc2") && list[1]!["filename"]!.GetValue<string>() == realFile,
              "entries are the new files; the non-ASCII file path arrives intact");
    }

    void Storm()
    {
        KillPlayers();
        Thread.Sleep(300);
        var procs = Enumerable.Range(0, 5).Select(i => Start(gui, [$"av://lavfi:testsrc=d=600:r={20 + i}"])).ToList();
        foreach (var p in procs)
        {
            p.WaitForExit(30000);
        }
        Thread.Sleep(500);
        Check(PlayerCount() == 1, $"one player for five launches (got {PlayerCount()})");
        using var ipc = Ipc.Connect(InstanceName);
        int n = ipc is null ? -1 : PlaylistCount(ipc);
        Check(n == 5, $"five playlist entries (got {n})");
    }

    void SeparateInstances()
    {
        int before = PlayerCount();
        int rc = RunLauncher(gui, ["--new-instance", "av://lavfi:smptebars=d=600"]).ExitCode;
        Check(rc == 0 && PlayerCount() == before + 1, $"--new-instance starts another player ({before} -> {PlayerCount()})");
        rc = RunLauncher(gui, ["--no-ui", "av://lavfi:rgbtestsrc=d=600"]).ExitCode;
        Thread.Sleep(1500);
        Check(rc == 0 && PlayerCount() == before + 2, $"--no-ui starts a plain player ({PlayerCount()})");
        KillPlayers();
    }

    void Console_()
    {
        var r = RunLauncher(con, ["--version"]);
        Check(r.ExitCode == 0 && r.StdOut.StartsWith("mpv "), $"mpv-animejanai.com --version: exit {r.ExitCode}, \"{r.StdOut.Split('\n')[0].Trim()}\"");
        r = RunLauncher(con, ["--no-such-option-xyz"]);
        Check(r.ExitCode == 1, $"a bad option exits 1 (got {r.ExitCode})");
    }

    void Registration()
    {
        string sub = @"Software\AnimeJaNaiLauncherTest-" + Guid.NewGuid().ToString("N")[..8];
        string? realBefore = RealAssociation();
        var env = new Dictionary<string, string> { ["ANIMEJANAI_TEST_HKCU"] = sub };
        try
        {
            int rc = RunLauncher(gui, ["--register", "video", "audio", "image", "path", "--default"], env).ExitCode;
            Check(rc == 0, $"--register exit 0 (got {rc})");
            using (var root = Registry.CurrentUser.OpenSubKey(sub))
            {
                string? cmd = root?.OpenSubKey(@"Software\Classes\AnimeJaNai.mpv\shell\open\command")?.GetValue("") as string;
                Check(cmd == $"{Quote(gui)} \"%1\"", $"video ProgID opens the launcher ({cmd})");
                Check(root?.OpenSubKey(@"Software\Classes\AnimeJaNai.mpv.audio") is not null && root?.OpenSubKey(@"Software\Classes\AnimeJaNai.mpv.image") is not null, "audio and image ProgIDs");
                Check(root?.OpenSubKey(@"Software\Classes\.mkv\OpenWithProgids")?.GetValueNames().Contains("AnimeJaNai.mpv") == true, ".mkv lists the ProgID in Open with");
                Check(root?.OpenSubKey(@"Software\Classes\.mkv")?.GetValue("") as string == "AnimeJaNai.mpv", "--default sets the per-user default");
                Check(root?.OpenSubKey(@"Software\Classes\Applications\mpv-animejanai.exe\SupportedTypes")?.GetValueNames().Contains(".flac") == true, "Applications entry lists supported types");
                string path = root?.OpenSubKey("Environment")?.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
                Check(path.Split(';').Contains(tree), "install folder added to the user PATH");
            }
            rc = RunLauncher(gui, ["--register", "video"], env).ExitCode;
            Check(rc == 0, "registering again is fine");
            rc = RunLauncher(gui, ["--unregister", "all"], env).ExitCode;
            Check(rc == 0, $"--unregister all exit 0 (got {rc})");
            using (var root = Registry.CurrentUser.OpenSubKey(sub))
            {
                var classes = root?.OpenSubKey(@"Software\Classes");
                // An empty "Applications" container may stay (in a real profile it always has others).
                var left = (classes?.GetSubKeyNames() ?? []).Where(k => k != "Applications" || classes!.OpenSubKey(k)!.SubKeyCount > 0).ToArray();
                Check(left.Length == 0, $"nothing left under Classes ({string.Join(", ", left.Take(5))})");
                string path = root?.OpenSubKey("Environment")?.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
                Check(!path.Split(';').Contains(tree), "install folder removed from PATH");
            }
            Check(RealAssociation() == realBefore, "the real HKCU association is untouched");
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
        }
    }

    void Dialog()
    {
        string dir = Path.GetDirectoryName(config)!;
        string pick = Path.Combine(dir, "テスト clip.mkv");
        string title = "AJN test " + Guid.NewGuid().ToString("N")[..6];
        var p = Start(gui, ["--open-dialog", "--title", title, "--multi", "--filter", "Video|*.mkv|All|*.*", "--initial", dir], capture: true);
        nint dlg = WaitDialog(title);
        Check(dlg != 0, "dialog opens");
        if (dlg != 0)
        {
            nint edit = FileNameEdit(dlg);
            SendMessageW(edit, 0x000C /* WM_SETTEXT */, 0, pick);
            SendMessageW(dlg, 0x0111 /* WM_COMMAND */, 1 /* IDOK */, 0);
        }
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10000);
        Check(p.ExitCode == 0 && output.Trim() == pick, $"OK prints the chosen path as UTF-8 (exit {p.ExitCode}, \"{output.Trim()}\")");

        title += "b";
        p = Start(gui, ["--open-dialog", "--title", title], capture: true);
        dlg = WaitDialog(title);
        if (dlg != 0)
        {
            SendMessageW(dlg, 0x0111, 2 /* IDCANCEL */, 0);
        }
        output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10000);
        Check(p.ExitCode == 1 && output.Length == 0, $"cancel exits 1 and prints nothing (exit {p.ExitCode})");
    }

    // ---- helpers ---------------------------------------------------------------------------------

    void WriteScratchConfig()
    {
        Directory.CreateDirectory(Path.Combine(config, "animejanai-ui"));
        File.WriteAllText(Path.Combine(config, "mpv.conf"),
            "pause=yes\nmute=yes\nkeep-open=yes\ngeometry=480x270+40+40\nosd-level=0\nsave-position-on-quit=no\n");
        File.WriteAllText(Path.Combine(config, "animejanai-ui", "main.lua"),
            "mp.set_property_native('user-data/test-ui/loaded', true)\n");
        File.WriteAllText(Path.Combine(config, "animejanai-ui", "menu.conf"), "Quit\tquit\n");
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(config)!, "テスト clip.mkv"), []);
    }

    sealed record Result(int ExitCode, string StdOut);

    Result RunLauncher(string exe, string[] a, Dictionary<string, string>? env = null)
    {
        var p = Start(exe, a, capture: true, env);
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(60000);
        return new Result(p.ExitCode, output);
    }

    Process Start(string exe, string[] a, bool capture = false, Dictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = capture };
        if (capture)
        {
            psi.StandardOutputEncoding = new UTF8Encoding(false);
        }
        foreach (var x in a)
        {
            psi.ArgumentList.Add(x);
        }
        psi.Environment["ANIMEJANAI_CONFIG_DIR"] = config;
        if (env is not null)
        {
            foreach (var (k, v) in env)
            {
                psi.Environment[k] = v;
            }
        }
        return Process.Start(psi)!;
    }

    List<Process> Players() => Process.GetProcessesByName("mpv").Where(p =>
    {
        try { return p.MainModule?.FileName.StartsWith(tree, StringComparison.OrdinalIgnoreCase) == true; }
        catch (Exception) { return false; }
    }).ToList();

    int PlayerCount() => Players().Count;

    void KillPlayers()
    {
        foreach (var p in Players())
        {
            try { p.Kill(); p.WaitForExit(5000); } catch (Exception) { }
        }
        try { File.Delete(Path.Combine(Path.GetTempPath(), InstanceName + ".last-launch")); } catch (IOException) { }
    }

    static int PlaylistCount(Ipc ipc) => ipc.Get("playlist-count")?.GetValue<int>() ?? -1;

    static long WaitWindow(Ipc ipc)
    {
        for (int i = 0; i < 100; i++)
        {
            try
            {
                if (ipc.Get("window-id")?.GetValue<long>() is long w && w != 0)
                {
                    return w;
                }
            }
            catch (InvalidOperationException) { }
            Thread.Sleep(50);
        }
        return 0;
    }

    static nint WaitDialog(string title)
    {
        for (int i = 0; i < 200; i++)
        {
            nint h = FindWindowW("#32770", title);
            if (h != 0)
            {
                Thread.Sleep(300); // let it finish building its controls
                return h;
            }
            Thread.Sleep(50);
        }
        return 0;
    }

    /// The file name box: control 1148 (a ComboBoxEx) holds a ComboBox that holds the Edit.
    static nint FileNameEdit(nint dlg)
    {
        nint box = GetDlgItem(dlg, 1148);
        nint combo = FindWindowExW(box, 0, "ComboBox", null);
        return FindWindowExW(combo != 0 ? combo : box, 0, "Edit", null);
    }

    static string? RealAssociation() =>
        Registry.CurrentUser.OpenSubKey(@"Software\Classes\AnimeJaNai.mpv\shell\open\command")?.GetValue("") as string ?? "(none)";

    static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

    void Section(string name, Action body)
    {
        Console.WriteLine($"\n== {name}");
        try
        {
            body();
        }
        catch (Exception e)
        {
            Check(false, $"threw: {e.GetType().Name}: {e.Message}");
        }
    }

    void Check(bool ok, string what)
    {
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}");
        if (ok) passed++; else failed++;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint FindWindowW(string cls, string title);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint FindWindowExW(nint parent, nint after, string cls, string? title);
    [LibraryImport("user32.dll")] private static partial nint GetDlgItem(nint dlg, int id);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint SendMessageW(nint h, uint msg, nint w, string l);
    [LibraryImport("user32.dll")] private static partial nint SendMessageW(nint h, uint msg, nint w, nint l);
}

/// Minimal mpv JSON IPC client for the checks.
sealed class Ipc : IDisposable
{
    readonly NamedPipeClientStream pipe;
    readonly StreamReader reader;
    int id;

    Ipc(NamedPipeClientStream p)
    {
        pipe = p;
        reader = new StreamReader(p, new UTF8Encoding(false), false, 4096, leaveOpen: true);
    }

    public static Ipc? Connect(string name, int timeoutMs = 2000)
    {
        var p = new NamedPipeClientStream(".", name, PipeDirection.InOut);
        try
        {
            p.Connect(timeoutMs);
            return new Ipc(p);
        }
        catch (Exception)
        {
            p.Dispose();
            return null;
        }
    }

    public JsonNode? Get(string property) => Send("get_property", property);

    public JsonNode? TryGet(string property)
    {
        try { return Get(property); }
        catch (InvalidOperationException) { return null; }
    }

    public bool TrySend(params object[] command)
    {
        try { Send(command); return true; }
        catch (InvalidOperationException) { return false; }
    }

    /// Runs one command; returns its data, throws when mpv reports an error.
    public JsonNode? Send(params object[] command)
    {
        int rid = ++id;
        var cmd = new JsonArray(command.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray());
        var line = Encoding.UTF8.GetBytes(new JsonObject { ["command"] = cmd, ["request_id"] = rid }.ToJsonString() + "\n");
        pipe.Write(line);
        pipe.Flush();
        while (reader.ReadLine() is { } reply)
        {
            var node = JsonNode.Parse(reply);
            if (node?["request_id"]?.GetValue<int>() != rid)
            {
                continue;
            }
            if (node["error"]?.GetValue<string>() != "success")
            {
                throw new InvalidOperationException($"{command[0]} {(command.Length > 1 ? command[1] : "")}: {node["error"]}");
            }
            return node["data"];
        }
        return null;
    }

    public void Dispose()
    {
        reader.Dispose();
        pipe.Dispose();
    }
}
