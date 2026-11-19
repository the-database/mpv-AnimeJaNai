using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// The interface scripts (portable_config/animejanai-ui, animejanai-ui-compat) under the launcher.
// The player gets this test program as its "launcher" (animejanai_ui-launcher): Stub.Run answers
// --open-dialog from a file the test writes, records --register calls instead of touching the
// registry, and passes --window-rect to the real launcher.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
sealed partial class WindowsTests
{
    string uiConfig = "";
    string stubFile = "";
    string stubLog = "";

    static string RepoConfig()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string p = Path.Combine(dir.FullName, "BuildMpvUpscale2xAnimeJaNai", "mpv-upscale-2x_animejanai", "portable_config");
            if (Directory.Exists(Path.Combine(p, "animejanai-ui")))
            {
                return p;
            }
        }
        throw new DirectoryNotFoundException("portable_config/animejanai-ui not found above the test program");
    }

    static string NameFor(string configDir) =>
        "mpv-animejanai-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(configDir.ToLowerInvariant())))[..8];

    void SetUpUiConfig(string? settingsXml = null)
    {
        string root = Path.Combine(Path.GetTempPath(), "ajn-ui-test-" + Guid.NewGuid().ToString("N")[..8]);
        uiConfig = Path.Combine(root, "portable_config");
        string repo = RepoConfig();
        CopyDir(Path.Combine(repo, "animejanai-ui"), Path.Combine(uiConfig, "animejanai-ui"));
        CopyDir(Path.Combine(repo, "animejanai-ui-compat"), Path.Combine(uiConfig, "animejanai-ui-compat"));
        File.WriteAllText(Path.Combine(uiConfig, "mpv.conf"), "pause=yes\nmute=no\nkeep-open=yes\nsave-position-on-quit=no\n");
        if (settingsXml is not null)
        {
            File.WriteAllText(Path.Combine(uiConfig, "settings.xml"), settingsXml);
        }
        stubFile = Path.Combine(root, "stub.txt");
        stubLog = Path.Combine(root, "stub.log");
    }

    static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from))
        {
            File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        }
    }

    /// Starts a player through the launcher with this program as the interface's launcher; returns
    /// an IPC connection (the launch has mpv options, so it is its own instance: <name>-<launcher pid>).
    Ipc? StartUiPlayer(params string[] extra)
    {
        var a = new List<string> { $"--script-opts-append=animejanai_ui-launcher={Environment.ProcessPath}" };
        a.AddRange(extra);
        a.Add("av://lavfi:testsrc=d=600");
        var env = new Dictionary<string, string>
        {
            ["ANIMEJANAI_CONFIG_DIR"] = uiConfig,
            ["AJN_TEST_STUB_FILE"] = stubFile,
            ["AJN_TEST_STUB_LOG"] = stubLog,
            ["AJN_TEST_REAL_LAUNCHER"] = gui,
        };
        var p = Start(gui, [.. a], capture: false, env);
        p.WaitForExit(30000);
        var ipc = Ipc.Connect($"{NameFor(uiConfig)}-{p.Id}");
        if (ipc is not null)
        {
            WaitFor(() => ipc.TryGet("user-data/animejanai_ui/loaded")?.GetValue<bool>() == true, 10000);
        }
        return ipc;
    }

    void Stub(int exit, params string[] paths) =>
        File.WriteAllText(stubFile, exit + "\n" + string.Join("\n", paths) + "\n", new UTF8Encoding(false));

    static bool WaitFor(Func<bool> cond, int ms = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            try
            {
                if (cond())
                {
                    return true;
                }
            }
            catch (Exception) { }
            Thread.Sleep(50);
        }
        return false;
    }

    static string LastMessage(Ipc ipc) => ipc.TryGet("user-data/animejanai_ui/last-message")?.GetValue<string>() ?? "";

    void Interface()
    {
        KillPlayers();
        SetUpUiConfig();
        using var ipc = StartUiPlayer();
        Check(ipc is not null, "player with the interface answers");
        if (ipc is null)
        {
            return;
        }
        Check(ipc.TryGet("user-data/animejanai_ui/loaded")?.GetValue<bool>() == true, "animejanai_ui loaded");

        // Dialog-backed messages.
        Stub(0, "av://lavfi:testsrc2=d=600", "av://lavfi:smptebars=d=600");
        ipc.Send("script-message-to", "animejanai_ui", "open-files");
        Check(WaitFor(() => Filenames(ipc) == "av://lavfi:testsrc2=d=600|av://lavfi:smptebars=d=600"), $"open-files replaces the playlist with the picks ({Filenames(ipc)})");
        Stub(0, "av://lavfi:rgbtestsrc=d=600");
        ipc.Send("script-message-to", "animejanai_ui", "add-files");
        Check(WaitFor(() => ipc.Get("playlist-count")!.GetValue<int>() == 3), "add-files appends");
        Check(WaitFor(() => LastMessage(ipc).StartsWith("Added 1 file")), $"add-files says so ({LastMessage(ipc)})");
        Stub(1);
        ipc.Send("script-message-to", "animejanai_ui", "open-files");
        Thread.Sleep(800);
        Check(ipc.Get("playlist-count")!.GetValue<int>() == 3, "cancel changes nothing");
        Stub(3);
        ipc.Send("script-message-to", "animejanai_ui", "open-files");
        Check(WaitFor(() => LastMessage(ipc).Contains("No file dialog")), $"no dialog program: the user is told ({LastMessage(ipc)})");
        Check(File.ReadAllText(stubLog).Contains("--open-dialog --owner "), "the dialog gets the player window as owner");

        string dir = Path.GetDirectoryName(uiConfig)!;
        string srt = Path.Combine(dir, "字幕.srt");
        File.WriteAllText(srt, "1\n00:00:00,000 --> 00:00:05,000\nhello\n");
        Stub(0, srt);
        ipc.Send("script-message-to", "animejanai_ui", "load-sub");
        Check(WaitFor(() => ipc.Get("track-list")!.AsArray().Any(t => t!["type"]!.GetValue<string>() == "sub" && t["external"]!.GetValue<bool>())), "load-sub adds an external subtitle track (non-ASCII name)");

        string disc = Path.Combine(dir, "disc");
        Directory.CreateDirectory(Path.Combine(disc, "BDMV"));
        Stub(0, Path.Combine(disc, "BDMV"));
        ipc.Send("script-message-to", "animejanai_ui", "open-disc");
        Check(WaitFor(() => ipc.Get("bluray-device")?.GetValue<string>() == disc), $"open-disc on a BDMV folder sets bluray-device to the disc root ({ipc.TryGet("bluray-device")})");

        // register goes to the launcher (stubbed: recorded, not run).
        ipc.Send("script-message-to", "animejanai_ui", "register", "video");
        Check(WaitFor(() => File.ReadAllText(stubLog).Contains("--register video --default")), "register video runs the launcher's --register");

        // Compat: old mpv.net messages.
        Stub(0, "av://lavfi:yuvtestsrc=d=600");
        ipc.Send("script-message-to", "mpvnet", "open-files", "append");
        Check(WaitFor(() => ipc.Get("playlist-count")!.GetValue<int>() == 2), "mpvnet open-files append reaches the dialog and appends");
        ipc.Send("set_property", "playlist-pos", 0);
        bool paused = ipc.Get("pause")!.GetValue<bool>();
        ipc.Send("script-message-to", "mpvnet", "play-pause");
        Check(WaitFor(() => ipc.Get("pause")!.GetValue<bool>() != paused), "mpvnet play-pause toggles pause");
        ipc.Send("set_property", "pause", true);

        // Quick bookmark.
        WaitFor(() => ipc.TryGet("duration") is not null, 5000);
        ipc.Send("set_property", "pause", true);
        WaitFor(() => ipc.Get("pause")!.GetValue<bool>());
        ipc.Send("seek", 10, "absolute+exact");
        WaitFor(() => Math.Abs(ipc.Get("time-pos")!.GetValue<double>() - 10) < 0.5);
        ipc.Send("script-message-to", "animejanai_ui", "quick-bookmark");
        WaitFor(() => LastMessage(ipc).StartsWith("Bookmark set"));
        ipc.Send("seek", 30, "absolute+exact");
        WaitFor(() => Math.Abs(ipc.Get("time-pos")!.GetValue<double>() - 30) < 0.5);
        ipc.Send("script-message-to", "animejanai_ui", "quick-bookmark");
        Check(WaitFor(() => Math.Abs(ipc.Get("time-pos")!.GetValue<double>() - 10) < 0.5), $"quick-bookmark returns to the mark ({ipc.TryGet("time-pos")})");

        // Clipboard (the user's clipboard text is put back afterwards).
        string? clip = ipc.TryGet("clipboard/text")?.GetValue<string>();
        if (clip is not null && ipc.TrySend("set_property", "clipboard/text", "av://lavfi:testsrc=d=600:r=7"))
        {
            ipc.Send("script-message-to", "animejanai_ui", "open-clipboard");
            Check(WaitFor(() => Filenames(ipc) == "av://lavfi:testsrc=d=600:r=7"), "open-clipboard loads the URL on the clipboard");
            ipc.TrySend("set_property", "clipboard/text", clip);
        }
        else
        {
            // No text on the clipboard (or not readable): the user's clipboard is left alone and
            // the "nothing to open" path is checked instead.
            ipc.Send("script-message-to", "animejanai_ui", "open-clipboard");
            Check(WaitFor(() => LastMessage(ipc).StartsWith("The clipboard has no URL")), $"open-clipboard without text says so ({LastMessage(ipc)})");
        }

        MenuChecks(ipc);
        ipc.Send("quit");
        WaitFor(() => PlayerCount() == 0, 5000);
    }

    void MenuChecks(Ipc ipc)
    {
        var expected = ParseMenuConf(Path.Combine(uiConfig, "animejanai-ui", "menu.conf"));
        var actual = new Dictionary<string, (string Cmd, string State)>();
        void Walk(JsonArray items, string prefix)
        {
            foreach (var it in items)
            {
                if (it is null || it["type"]?.GetValue<string>() == "separator")
                {
                    continue;
                }
                string title = prefix + (it["title"]?.GetValue<string>() ?? "");
                string cmd = it["cmd"]?.GetValue<string>() ?? "";
                string state = it["state"]?.ToJsonString() ?? "";
                actual.TryAdd(title, (cmd, state));
                if (it["submenu"] is JsonArray sub)
                {
                    Walk(sub, title + " > ");
                }
            }
        }
        JsonNode? Data() => ipc.Get("menu-data");
        Walk(Data()!.AsArray(), "");
        var missing = expected.Where(e => !actual.TryGetValue(e.Key, out var a) || a.Cmd != e.Value).Select(e => e.Key).ToList();
        Check(missing.Count == 0 && expected.Count > 100, $"menu-data matches menu.conf: {expected.Count} commands ({string.Join(", ", missing.Take(5))})");

        string State(string path)
        {
            actual.Clear();
            Walk(Data()!.AsArray(), "");
            return actual.TryGetValue(path, out var a) ? a.State : "(missing)";
        }
        Check(State("AnimeJaNai > Install Update").Contains("hidden"), "Install Update hidden while no update is known");
        ipc.Send("set_property", "user-data/animejanai/update-available", "9.9.9");
        Check(WaitFor(() => !State("AnimeJaNai > Install Update").Contains("hidden")), "Install Update shown once an update is known");
        Check(!State("View > Toggle On Top").Contains("checked"), "On Top unchecked");
        ipc.Send("set_property", "ontop", true);
        Check(WaitFor(() => State("View > Toggle On Top").Contains("checked")), "On Top checked after turning it on");
        ipc.Send("set_property", "ontop", false);
        ipc.Send("set_property", "loop-file", "inf");
        Check(WaitFor(() => State("Tools > Toggle infinite file looping").Contains("checked")), "loop toggle checked with loop-file=inf");
    }

    /// "A > B" path -> command, for menu.conf items that run a command.
    static Dictionary<string, string> ParseMenuConf(string path)
    {
        var result = new Dictionary<string, string>();
        var stack = new List<string>();
        foreach (var raw in File.ReadAllLines(path))
        {
            if (raw.StartsWith('#') || raw.Trim().Length == 0)
            {
                continue;
            }
            int depth = raw.TakeWhile(c => c == '\t').Count();
            var fields = raw.Trim('\t').Split('\t', StringSplitOptions.RemoveEmptyEntries);
            while (stack.Count > depth)
            {
                stack.RemoveAt(stack.Count - 1);
            }
            stack.Add(fields[0]);
            string key = string.Join(" > ", stack);
            if (fields.Length > 1 && !fields[1].StartsWith('$') && !fields[1].Contains('='))
            {
                result[key] = fields[1];
            }
        }
        return result;
    }

    static string Filenames(Ipc ipc) =>
        string.Join("|", ipc.Get("playlist")!.AsArray().Select(e => e!["filename"]!.GetValue<string>()));

    void RememberedState()
    {
        KillPlayers();
        SetUpUiConfig();
        var ipc = StartUiPlayer();
        Check(ipc is not null, "player started");
        if (ipc is null)
        {
            return;
        }
        ipc.Send("set_property", "volume", 37);
        ipc.Send("set_property", "mute", true);
        ipc.Send("set_property", "geometry", "400x226+130+140");
        Thread.Sleep(800);
        string rectBefore = RunLauncher(gui, ["--window-rect", ipc.Get("window-id")!.GetValue<long>().ToString()]).StdOut.Trim();
        ipc.Send("quit");
        ipc.Dispose();
        WaitFor(() => PlayerCount() == 0, 5000);
        string statePath = Path.Combine(uiConfig, "animejanai-ui-state.json");
        var state = File.Exists(statePath) ? JsonNode.Parse(File.ReadAllText(statePath)) : null;
        Check(state is not null, $"state saved at quit ({state?.ToJsonString()})");
        Check(state?["volume"]?.GetValue<double>() == 37 && state?["mute"]?.GetValue<bool>() == true, "volume and mute saved");
        Check($"{state?["x"]} {state?["y"]} {state?["width"]} {state?["height"]}" == rectBefore,
              $"window position and size saved as --window-rect reports them ({rectBefore})");

        ipc = StartUiPlayer();
        if (ipc is null)
        {
            Check(false, "restarted");
            return;
        }
        Thread.Sleep(800);
        Check(ipc.Get("volume")!.GetValue<double>() == 37 && ipc.Get("mute")!.GetValue<bool>(), "volume and mute restored");
        string rectAfter = RunLauncher(gui, ["--window-rect", ipc.Get("window-id")!.GetValue<long>().ToString()]).StdOut.Trim();
        Check(rectAfter == rectBefore, $"window comes back at the same place and size ({rectAfter} vs {rectBefore})");
        ipc.Send("quit");
        ipc.Dispose();
        WaitFor(() => PlayerCount() == 0, 5000);

        // First run with an mpv.net settings.xml and no state yet: imported.
        SetUpUiConfig("<?xml version=\"1.0\"?><AppSettings><Volume>55</Volume><Mute>yes</Mute><AudioDevice /></AppSettings>");
        ipc = StartUiPlayer();
        if (ipc is not null)
        {
            Thread.Sleep(500);
            Check(ipc.Get("volume")!.GetValue<double>() == 55 && ipc.Get("mute")!.GetValue<bool>(), "mpv.net's volume and mute imported on the first run");
            ipc.Send("quit");
            ipc.Dispose();
        }
        WaitFor(() => PlayerCount() == 0, 5000);
    }
}

/// This test program, run as the interface's "launcher".
static class StubLauncher
{
    public static int Run(string[] args)
    {
        if (Environment.GetEnvironmentVariable("AJN_TEST_STUB_LOG") is { Length: > 0 } log)
        {
            File.AppendAllText(log, string.Join(' ', args) + "\n");
        }
        switch (args[0])
        {
            case "--check":
                Console.Out.WriteLine(Environment.GetEnvironmentVariable("AJN_TEST_CHECK_OUTPUT") ?? "");
                return 0;
            case "--open-dialog":
            {
                var lines = File.ReadAllLines(Environment.GetEnvironmentVariable("AJN_TEST_STUB_FILE")!, Encoding.UTF8);
                using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { NewLine = "\n" };
                foreach (var l in lines.Skip(1).Where(l => l.Length > 0))
                {
                    stdout.WriteLine(l);
                }
                return int.Parse(lines[0]);
            }
            case "--register":
            case "--unregister":
                return 0;
            default:
            {
                var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("AJN_TEST_REAL_LAUNCHER")!) { RedirectStandardOutput = true };
                foreach (var a in args)
                {
                    psi.ArgumentList.Add(a);
                }
                using var p = Process.Start(psi)!;
                Console.Out.Write(p.StandardOutput.ReadToEnd());
                p.WaitForExit();
                return p.ExitCode;
            }
        }
    }
}
