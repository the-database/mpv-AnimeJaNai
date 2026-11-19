// Linux checks for the mpv-animejanai launcher (entry point and usage: Program.cs).
//
// On Linux the launcher execs mpv/mpv, so a launch that starts a player never exits: the launcher
// process *is* the player. A launch that hands its files to a running player exits 0.
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

[SupportedOSPlatform("linux")]
sealed class LinuxTests(string tree, string launcherDir) : TestBase
{
    readonly string launcher = Path.Combine(tree, "mpv-animejanai");
    string Scratch => Path.GetDirectoryName(config)!;
    string RunDir => Path.Combine(Scratch, "run");
    string LogFile => Path.Combine(Scratch, "launcher.log");
    const string CallerLibPath = "/nonexistent/caller-lib";
    readonly List<Process> started = [];

    public int Run(bool onlyRegistry)
    {
        File.Copy(Path.Combine(launcherDir, "mpv-animejanai"), launcher, overwrite: true);
        WriteScratchConfig();
        try
        {
            Section("--register desktop / --unregister desktop (scratch XDG dirs)", Desktop);
            Section("installer/linux scripts call the launcher (scratch XDG dirs)", InstallerScripts);
            Section("--open-dialog through stub zenity / kdialog", Dialog);
            Section("--window-rect", WindowRect);
            if (!onlyRegistry)
            {
                if (!File.Exists(Path.Combine(tree, "mpv", "mpv")))
                {
                    Check(false, $"{Path.Combine(tree, "mpv", "mpv")} is missing (see the usage in Program.cs)");
                }
                else
                {
                    Section("exec pass-through (mpv --version, a bad option)", PassThrough);
                    Section("first launch: exec, library path, config dir, interface, app id", FirstLaunch);
                    Section("hand-off to the running player", HandOff);
                    Section("five launchers at once", Storm);
                    Section("a player killed without cleanup (stale socket)", StaleSocket);
                    Section("--new-instance and --no-ui", SeparateInstances);
                }
            }
        }
        finally
        {
            KillPlayers();
            foreach (var p in started)
            {
                try { if (!p.HasExited) p.Kill(); } catch (Exception) { }
            }
            try { Directory.Delete(Scratch, true); } catch (IOException) { }
        }
        return Summary();
    }

    // ---- player sections -----------------------------------------------------------------------

    void PassThrough()
    {
        var r = RunLauncher(["--version"]);
        Check(r.ExitCode == 0 && r.StdOut.StartsWith("mpv "), $"--version: exit {r.ExitCode}, \"{r.StdOut.Split('\n')[0].Trim()}\" (the bundled libraries load)");
        r = RunLauncher(["--no-such-option-xyz"]);
        Check(r.ExitCode == 1, $"a bad option exits 1, as mpv does (got {r.ExitCode})");
    }

    void FirstLaunch()
    {
        KillPlayers();
        var sw = Stopwatch.StartNew();
        var p = Start(["av://lavfi:testsrc=d=600"]);
        using var ipc = Ipc.Connect(InstanceName, 10000, RunDir);
        long ms = sw.ElapsedMilliseconds;
        Check(ipc is not null, $"player answers on {InstanceName}.sock, {ms} ms after the launcher started");
        if (ipc is null)
        {
            return;
        }
        var players = Players();
        Check(players.Count == 1, $"one player (got {players.Count})");
        Check(players.Contains(p.Id) && !p.HasExited, "the launcher process became the player (execve, same pid)");
        string exe = new FileInfo($"/proc/{p.Id}/exe").LinkTarget ?? "";
        string realMpv = Path.GetFullPath(new FileInfo(Path.Combine(tree, "mpv", "mpv")).ResolveLinkTarget(true)?.FullName ?? Path.Combine(tree, "mpv", "mpv"));
        Check(exe == realMpv, $"its image is mpv/mpv ({exe})");
        Check(PlaylistCount(ipc) == 1, "playlist has the one file");

        var cmd = CommandLine(p.Id);
        Check(cmd.Contains($"--config-dir={config}"), "--config-dir is the scratch config (ANIMEJANAI_CONFIG_DIR)");
        Check(cmd.Contains($"--input-ipc-server={Path.Combine(RunDir, InstanceName + ".sock")}"), "IPC socket is $XDG_RUNTIME_DIR/<instance>.sock");
        Check(cmd.Contains("--player-operation-mode=pseudo-gui"), "no terminal: pseudo-gui mode");
        Check(ipc.Get("wayland-app-id")?.GetValue<string>() == "mpv-animejanai", "wayland-app-id = mpv-animejanai");
        Check(ipc.Get("x11-name")?.GetValue<string>() == "mpv-animejanai", "x11-name = mpv-animejanai");
        Check(ipc.Get("user-data/test-ui/loaded")?.GetValue<bool>() == true, "interface script loaded from <config>/animejanai-ui");
        string opts = ipc.Get("script-opts")?.ToJsonString() ?? "";
        Check(opts.Contains("select-menu_conf_path") && opts.Contains("menu.conf"), "menu.conf path passed to select.lua");
        Check(opts.Contains($"\"animejanai_ui-launcher\":\"{launcher}\""), "launcher path passed to the interface");

        var env = Environ(p.Id);
        string extra = Environment.GetEnvironmentVariable("ANIMEJANAI_EXTRA_LIBS") ?? "";
        string expected = string.Join(':', new[] { Path.Combine(tree, "mpv"), Path.Combine(tree, "animejanai", "inference") }
            .Concat(extra.Split(':', StringSplitOptions.RemoveEmptyEntries)).Append(CallerLibPath));
        Check(env.GetValueOrDefault("LD_LIBRARY_PATH") == expected, $"LD_LIBRARY_PATH = bundled + ANIMEJANAI_EXTRA_LIBS + caller's ({env.GetValueOrDefault("LD_LIBRARY_PATH")})");
        Check(env.GetValueOrDefault("ANIMEJANAI_ORIG_LD_LIBRARY_PATH") == CallerLibPath, "the caller's value is kept in ANIMEJANAI_ORIG_LD_LIBRARY_PATH");
    }

    void HandOff()
    {
        Thread.Sleep(1800); // past the append window: a new launch replaces
        string realFile = Path.Combine(Scratch, "テスト clip.mkv");
        var sw = Stopwatch.StartNew();
        int rc = RunLauncher(["av://lavfi:testsrc2=d=600", realFile]).ExitCode;
        Check(rc == 0, $"forwarding launcher exits 0, {sw.ElapsedMilliseconds} ms");
        Check(Players().Count == 1, "still one player");
        using var ipc = Ipc.Connect(InstanceName, 2000, RunDir)!;
        var list = ipc.Get("playlist")!.AsArray();
        Check(list.Count == 2, $"replace + append leaves two entries (got {list.Count})");
        Check(list.Count == 2 && list[0]!["filename"]!.GetValue<string>().Contains("testsrc2") && list[1]!["filename"]!.GetValue<string>() == realFile,
              "entries are the new files; the non-ASCII file path arrives intact");
        // A relative path is made absolute against the launcher's working folder.
        var r = RunLauncher([Path.GetFileName(realFile)], cwd: Scratch);
        list = ipc.Get("playlist")!.AsArray();
        Check(r.ExitCode == 0 && list[^1]!["filename"]!.GetValue<string>() == realFile, "a relative path arrives absolute");
    }

    void Storm()
    {
        KillPlayers();
        Thread.Sleep(300);
        var sw = Stopwatch.StartNew();
        var procs = Enumerable.Range(0, 5).Select(i => Start([$"av://lavfi:testsrc=d=600:r={20 + i}"])).ToList();
        // Four hand their file over and exit; one has become the player and stays.
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < 30000 && procs.Count(p => p.HasExited) < 4)
        {
            Thread.Sleep(20);
        }
        long ms = sw.ElapsedMilliseconds;
        var exited = procs.Where(p => p.HasExited).ToList();
        Check(exited.Count == 4 && exited.All(p => p.ExitCode == 0), $"four launchers forwarded and exited 0 within {ms} ms (exited {exited.Count}: {string.Join(',', exited.Select(p => p.ExitCode))})");
        var players = Players();
        Check(players.Count == 1, $"one player for five launches (got {players.Count})");
        Check(players.Count == 1 && procs.Single(p => !p.HasExited).Id == players[0], "the one left running is the player");
        using var ipc = Ipc.Connect(InstanceName, 2000, RunDir);
        int n = ipc is null ? -1 : PlaylistCount(ipc);
        Check(n == 5, $"five playlist entries (got {n})");
    }

    void StaleSocket()
    {
        KillPlayers(); // SIGKILL: mpv cannot remove its socket
        string sock = Path.Combine(RunDir, InstanceName + ".sock");
        Check(File.Exists(sock), "the killed player's socket file is left behind");
        Start(["av://lavfi:testsrc=d=600"]);
        using var ipc = Ipc.Connect(InstanceName, 10000, RunDir);
        Check(ipc is not null && Players().Count == 1, "the next launch starts a new player on it");
    }

    void SeparateInstances()
    {
        int before = Players().Count;
        // As if the interface started it: LD_LIBRARY_PATH already has the bundled folders.
        string inPlayer = $"{Path.Combine(tree, "mpv")}:{Path.Combine(tree, "animejanai", "inference")}:{CallerLibPath}";
        var p = Start(["--new-instance", "av://lavfi:smptebars=d=600"],
                      new() { ["LD_LIBRARY_PATH"] = inPlayer, ["ANIMEJANAI_ORIG_LD_LIBRARY_PATH"] = CallerLibPath });
        bool up = WaitFor(() => Players().Count == before + 1);
        Check(up, $"--new-instance starts another player ({before} -> {Players().Count})");
        var cmd = CommandLine(p.Id);
        Check(cmd.Contains($"--input-ipc-server={Path.Combine(RunDir, $"{InstanceName}-{p.Id}.sock")}"), "on a pid-suffixed socket (the launcher's pid, which is the player's)");
        string ld = Environ(p.Id).GetValueOrDefault("LD_LIBRARY_PATH") ?? "";
        Check(ld.Split(':').Count(x => x == Path.Combine(tree, "mpv")) == 1 && ld.EndsWith(CallerLibPath),
              $"started from inside a player, the bundled folders are not added twice ({ld})");

        var q = Start(["--no-ui", "av://lavfi:rgbtestsrc=d=600"]);
        up = WaitFor(() => Players().Count == before + 2);
        Check(up, $"--no-ui starts a plain player ({Players().Count})");
        cmd = CommandLine(q.Id);
        Check(!cmd.Any(a => a.StartsWith("--script=") || a.StartsWith("--input-ipc-server") || a.StartsWith("--wayland-app-id")) && cmd.Contains($"--config-dir={config}"),
              "--no-ui: same config dir, no interface, no socket, no app id");
        KillPlayers();
    }

    // ---- no-player sections --------------------------------------------------------------------

    void Desktop()
    {
        string data = Path.Combine(Scratch, "xdg-data"), cfg = Path.Combine(Scratch, "xdg-config");
        string desktop = Path.Combine(data, "applications", "mpv-animejanai.desktop");
        string icon = Path.Combine(data, "icons", "hicolor", "256x256", "apps", "mpv-animejanai.png");
        string mimeapps = Path.Combine(cfg, "mimeapps.list");
        Directory.CreateDirectory(cfg);
        string before = "# kept\n[Default Applications]\nvideo/mp4=other-player.desktop;\ntext/plain=editor.desktop;\n\n[Added Associations]\nvideo/webm=other-player.desktop;\n";
        File.WriteAllText(mimeapps, before);
        var env = new Dictionary<string, string?> { ["XDG_DATA_HOME"] = data, ["XDG_CONFIG_HOME"] = cfg };

        var r = RunLauncher(["--register", "desktop", "--default"], env: env);
        Check(r.ExitCode == 0 && File.Exists(desktop), $"--register desktop --default writes the entry (exit {r.ExitCode})");
        string text = File.Exists(desktop) ? File.ReadAllText(desktop) : "";
        Check(text.Contains($"\nExec={launcher} -- %U\n") && text.Contains($"\nTryExec={launcher}\n"), "Exec / TryExec start this launcher");
        Check(text.Contains("\nStartupWMClass=mpv-animejanai\n"), "StartupWMClass matches the player's app id");
        Check(text.Contains("\nIcon=mpv\n") && !File.Exists(icon), "no artwork in the tree: Icon=mpv, nothing copied");
        Validate(desktop);
        string list = File.ReadAllText(mimeapps);
        Check(list.Contains("\nvideo/mp4=mpv-animejanai.desktop;other-player.desktop;\n"), "--default puts the entry first for video/mp4, keeping the other player after it");
        Check(list.Contains("\nvideo/x-matroska=mpv-animejanai.desktop;\n") && list.Contains("\ntext/plain=editor.desktop;\n") && list.StartsWith("# kept\n"),
              "adds the other video types; unrelated lines stay");

        // Artwork at the tree root is installed into the icon theme.
        string png = Path.Combine(tree, "mpv-animejanai.png");
        File.WriteAllBytes(png, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="));
        try
        {
            r = RunLauncher(["--register", "desktop"], env: env);
            text = File.ReadAllText(desktop);
            Check(r.ExitCode == 0 && text.Contains("\nIcon=mpv-animejanai\n") && File.Exists(icon), "with mpv-animejanai.png in the tree: Icon=mpv-animejanai, icon copied");
        }
        finally
        {
            File.Delete(png);
        }

        // An --exec path that needs quoting (the AppImage passes its own file).
        string odd = Path.Combine(Scratch, "My Apps $x", "mpv AnimeJaNai.AppImage");
        r = RunLauncher(["--register", "desktop", "--exec", odd], env: env);
        text = File.ReadAllText(desktop);
        Check(r.ExitCode == 0 && text.Contains($"\nExec=\"{odd.Replace("$", "\\\\$")}\" -- %U\n"), "--exec path with a space and a $ is quoted by the Desktop Entry rules");
        Validate(desktop);

        r = RunLauncher(["--unregister", "desktop"], env: env);
        Check(r.ExitCode == 0 && !File.Exists(desktop) && !File.Exists(icon), $"--unregister desktop removes the entry and the icon (exit {r.ExitCode})");
        list = File.ReadAllText(mimeapps);
        Check(!list.Contains("mpv-animejanai"), "no mpv-animejanai.desktop left in mimeapps.list");
        Check(list == before, $"mimeapps.list is back as it was ({list.ReplaceLineEndings("\\n")})");
        r = RunLauncher(["--unregister", "all"], env: env);
        Check(r.ExitCode == 0, "unregistering again (all) is fine");

        r = RunLauncher(["--register", "video"], env: env);
        Check(r.ExitCode == 2 && r.StdErr.Contains("desktop"), $"--register video on Linux fails clearly (exit {r.ExitCode}: {r.StdErr.Trim()})");
    }

    /// install.sh / uninstall.sh / AppRun --install / --uninstall from this checkout, with the
    /// launcher binary (they call --register desktop) and with the old shell-script launcher
    /// (they write the entry themselves, as before).
    void InstallerScripts()
    {
        string? repo = AppContext.BaseDirectory;
        while (repo is not null && !File.Exists(Path.Combine(repo, "installer", "linux", "install.sh")))
        {
            repo = Path.GetDirectoryName(repo.TrimEnd('/'));
        }
        if (repo is null)
        {
            Console.WriteLine("  SKIP  installer/linux not found above the test binary");
            return;
        }
        foreach (bool binary in new[] { true, false })
        {
            string kind = binary ? "binary" : "shell script";
            string root = Path.Combine(Scratch, binary ? "inst-bin" : "inst-sh");
            string t = Path.Combine(root, "tree");
            Directory.CreateDirectory(Path.Combine(t, "mpv"));
            const UnixFileMode Exec = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            if (binary)
            {
                File.Copy(launcher, Path.Combine(t, "mpv-animejanai"));
            }
            else
            {
                File.WriteAllText(Path.Combine(t, "mpv-animejanai"), "#!/bin/sh\nexec \"$(dirname \"$0\")/mpv/mpv\" \"$@\"\n");
            }
            File.SetUnixFileMode(Path.Combine(t, "mpv-animejanai"), Exec);
            foreach (var s in new[] { "install.sh", "uninstall.sh", "AppRun" })
            {
                File.Copy(Path.Combine(repo, "installer", "linux", s), Path.Combine(t, s));
                File.SetUnixFileMode(Path.Combine(t, s), Exec);
            }
            var env = new Dictionary<string, string?>
            {
                ["XDG_DATA_HOME"] = Path.Combine(root, "data"), ["XDG_CONFIG_HOME"] = Path.Combine(root, "config"),
                ["XDG_BIN_HOME"] = Path.Combine(root, "bin"), ["APPIMAGE"] = null,
            };
            string desktop = Path.Combine(root, "data", "applications", "mpv-animejanai.desktop");
            string link = Path.Combine(root, "bin", "mpv-animejanai");
            string mimeapps = Path.Combine(root, "config", "mimeapps.list");
            string Text(string p) => File.Exists(p) ? File.ReadAllText(p) : "";

            var r = RunProgram(Path.Combine(t, "install.sh"), [], env);
            Check(r.ExitCode == 0 && Text(desktop).Contains($"\nExec={Path.Combine(t, "mpv-animejanai")} ") && File.Exists(link),
                  $"{kind}: install.sh writes the entry and the symlink (exit {r.ExitCode} {r.StdErr.Trim()})");
            if (binary)
            {
                Check(Text(desktop).Contains("\nStartupWMClass=mpv-animejanai\n") && Text(mimeapps).Contains("\nvideo/mp4=mpv-animejanai.desktop;"),
                      $"{kind}: the launcher wrote it, with the defaults");
            }
            Validate(desktop);
            r = RunProgram(Path.Combine(t, "uninstall.sh"), [], env);
            Check(r.ExitCode == 0 && !File.Exists(desktop) && !File.Exists(link) && (!binary || !Text(mimeapps).Contains("mpv-animejanai")),
                  $"{kind}: uninstall.sh removes them{(binary ? ", defaults included" : "")} (exit {r.ExitCode} {r.StdErr.Trim()})");

            string appimage = Path.Combine(root, "My Player.AppImage");
            File.WriteAllBytes(appimage, []);
            env["APPIMAGE"] = appimage;
            r = RunProgram(Path.Combine(t, "AppRun"), ["--install"], env);
            Check(r.ExitCode == 0 && Text(desktop).Contains($"\nTryExec={appimage}\n") && (!binary || !Text(mimeapps).Contains("mpv-animejanai")),
                  $"{kind}: AppRun --install points the entry at the AppImage file, no defaults (exit {r.ExitCode} {r.StdErr.Trim()})");
            Validate(desktop);
            r = RunProgram(Path.Combine(t, "AppRun"), ["--install", "--default"], env);
            if (binary)
            {
                Check(r.ExitCode == 0 && Text(mimeapps).Contains("\nvideo/x-matroska=mpv-animejanai.desktop;"), $"{kind}: AppRun --install --default sets the defaults");
            }
            r = RunProgram(Path.Combine(t, "AppRun"), ["--uninstall"], env);
            Check(r.ExitCode == 0 && !File.Exists(desktop) && (!binary || !Text(mimeapps).Contains("mpv-animejanai")),
                  $"{kind}: AppRun --uninstall removes it (exit {r.ExitCode} {r.StdErr.Trim()})");
        }
    }

    static Result RunProgram(string exe, string[] a, Dictionary<string, string?> env)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var x in a)
        {
            psi.ArgumentList.Add(x);
        }
        foreach (var (k, v) in env)
        {
            if (v is null) psi.Environment.Remove(k); else psi.Environment[k] = v;
        }
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return new Result(p.ExitCode, output, err.Result);
    }

    void Validate(string desktop)
    {
        if (Which("desktop-file-validate") is not string v)
        {
            Console.WriteLine("  SKIP  desktop-file-validate is not installed");
            return;
        }
        var psi = new ProcessStartInfo(v) { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(desktop);
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        Check(p.ExitCode == 0 && output.Trim().Length == 0, $"desktop-file-validate: exit {p.ExitCode}{(output.Trim().Length > 0 ? ", " + output.Trim() : ", no remarks")}");
    }

    void Dialog()
    {
        string stubs = Path.Combine(Scratch, "stubs");
        string zdir = Path.Combine(stubs, "z"), kdir = Path.Combine(stubs, "k"), both = Path.Combine(stubs, "both"), none = Path.Combine(stubs, "none");
        foreach (var d in new[] { zdir, kdir, both, none })
        {
            Directory.CreateDirectory(d);
        }
        WriteStub(Path.Combine(zdir, "zenity"));
        WriteStub(Path.Combine(kdir, "kdialog"));
        WriteStub(Path.Combine(both, "zenity"));
        WriteStub(Path.Combine(both, "kdialog"));
        string stubLog = Path.Combine(stubs, "log");
        string a = Path.Combine(Scratch, "テスト clip.mkv"), b = Path.Combine(Scratch, "second file.mp4");

        Dictionary<string, string?> Env(string path, string mode, string? orig = CallerLibPath) => new()
        {
            ["PATH"] = path, ["STUB_MODE"] = mode, ["STUB_LOG"] = stubLog, ["STUB_OUT1"] = a, ["STUB_OUT2"] = b,
            ["LD_LIBRARY_PATH"] = "/bundled/mpv:/bundled/inference", ["ANIMEJANAI_ORIG_LD_LIBRARY_PATH"] = orig,
            ["XDG_CURRENT_DESKTOP"] = "GNOME", ["WAYLAND_DISPLAY"] = null,
        };
        string[] filesArgs = ["--open-dialog", "--multi", "--title", "Open files", "--filter", "Video|*.mkv;*.mp4|All|*.*", "--initial", Scratch];

        var r = RunLauncher(filesArgs, env: Env(zdir, "ok"));
        var log = StubLog(stubLog);
        Check(r.ExitCode == 0 && r.StdOut == $"{a}\n{b}\n", $"zenity: prints the chosen paths as UTF-8, one per line (exit {r.ExitCode})");
        Check(log.Contains("arg=--file-selection") && log.Contains("arg=--multiple") && log.Contains("arg=--title=Open files")
              && log.Contains("arg=--file-filter=Video | *.mkv *.mp4") && log.Contains("arg=--file-filter=All | *")
              && log.Contains($"arg=--filename={Scratch}/"), $"zenity arguments: multiple, title, filters, start folder ({string.Join("; ", log.Where(l => l.StartsWith("arg=")))})");
        Check(log.Contains($"LD={CallerLibPath}") && log.Contains("ORIG=<unset>"), "zenity runs with the caller's LD_LIBRARY_PATH restored");

        r = RunLauncher(["--open-dialog", "--folder"], env: Env(zdir, "ok", orig: ""));
        log = StubLog(stubLog);
        Check(r.ExitCode == 0 && log.Contains("arg=--directory") && !log.Any(l => l.StartsWith("arg=--file-filter")), "--folder: zenity --directory, no filters");
        Check(log.Contains("LD=<unset>"), "a caller without LD_LIBRARY_PATH: the helper gets none");

        r = RunLauncher(filesArgs, env: Env(zdir, "cancel"));
        Check(r.ExitCode == 1 && r.StdOut.Length == 0, $"cancel exits 1 and prints nothing (exit {r.ExitCode})");
        r = RunLauncher(filesArgs, env: Env(zdir, "fail"));
        Check(r.ExitCode == 2 && r.StdOut.Length == 0, $"a failing dialog program is an error, not a cancel (exit {r.ExitCode})");
        r = RunLauncher(filesArgs, env: Env(none, "ok"));
        Check(r.ExitCode == 3 && r.StdOut.Length == 0 && r.StdErr.Contains("zenity"), $"no dialog program: exit 3 ({r.StdErr.Trim()})");

        r = RunLauncher(filesArgs, env: Env(both, "ok"));
        Check(r.ExitCode == 0 && StubLog(stubLog).Contains($"prog={Path.Combine(both, "zenity")}"), "both installed, not KDE: zenity");
        var kde = Env(both, "ok");
        kde["XDG_CURRENT_DESKTOP"] = "KDE";
        r = RunLauncher(filesArgs, env: kde);
        log = StubLog(stubLog);
        Check(r.ExitCode == 0 && r.StdOut == $"{a}\n{b}\n" && log.Contains($"prog={Path.Combine(both, "kdialog")}"), "XDG_CURRENT_DESKTOP=KDE: kdialog first");
        Check(log.Contains("arg=--getopenfilename") && log.Contains($"arg={Scratch}") && log.Contains("arg=--multiple") && log.Contains("arg=--separate-output") && log.Contains("arg=Open files"),
              "kdialog arguments: getopenfilename, start folder, multiple, separate-output, title");
        r = RunLauncher(["--open-dialog", "--owner", "12345"], env: Env(kdir, "ok"));
        log = StubLog(stubLog);
        Check(r.ExitCode == 0 && log.Contains($"prog={Path.Combine(kdir, "kdialog")}") && log.Contains("arg=--attach") && log.Contains("arg=12345"),
              "only kdialog: used; parented with --attach on X11");
    }

    void WindowRect()
    {
        var r = RunLauncher(["--window-rect", "1"]);
        Check(r.ExitCode == 2 && r.StdErr.Contains("Windows-only"), $"--window-rect errors clearly ({r.StdErr.Trim()})");
    }

    // ---- helpers -------------------------------------------------------------------------------

    void WriteScratchConfig()
    {
        Directory.CreateDirectory(Path.Combine(config, "animejanai-ui"));
        Directory.CreateDirectory(RunDir);
        File.SetUnixFileMode(RunDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(config, "mpv.conf"),
            "vo=null\nao=null\nidle=yes\nkeep-open=yes\npause=yes\nmute=yes\nosd-level=0\nsave-position-on-quit=no\nterminal=no\n");
        File.WriteAllText(Path.Combine(config, "animejanai-ui", "main.lua"),
            "mp.set_property_native('user-data/test-ui/loaded', true)\n");
        File.WriteAllText(Path.Combine(config, "animejanai-ui", "menu.conf"), "Quit\tquit\n");
        File.WriteAllBytes(Path.Combine(Scratch, "テスト clip.mkv"), []);
    }

    static void WriteStub(string path)
    {
        File.WriteAllText(path,
            "#!/bin/sh\n" +
            "{ printf 'prog=%s\\n' \"$0\"; for a in \"$@\"; do printf 'arg=%s\\n' \"$a\"; done\n" +
            "  printf 'LD=%s\\n' \"${LD_LIBRARY_PATH-<unset>}\"; printf 'ORIG=%s\\n' \"${ANIMEJANAI_ORIG_LD_LIBRARY_PATH-<unset>}\"; } > \"$STUB_LOG\"\n" +
            "case \"$STUB_MODE\" in cancel) exit 1 ;; fail) exit 5 ;; esac\n" +
            "printf '%s\\n%s\\n' \"$STUB_OUT1\" \"$STUB_OUT2\"\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    static List<string> StubLog(string path) => File.Exists(path) ? File.ReadAllLines(path).ToList() : [];

    sealed record Result(int ExitCode, string StdOut, string StdErr);

    Result RunLauncher(string[] a, Dictionary<string, string?>? env = null, string? cwd = null)
    {
        using var p = Start(a, env, capture: true, cwd: cwd);
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(60000);
        return new Result(p.ExitCode, output, err.Result);
    }

    /// Starts the launcher. Without capture its output goes to this console: a launch that becomes
    /// a player must not write into a pipe nobody reads.
    Process Start(string[] a, Dictionary<string, string?>? env = null, bool capture = false, string? cwd = null)
    {
        var psi = new ProcessStartInfo(launcher) { UseShellExecute = false, RedirectStandardOutput = capture, RedirectStandardError = capture };
        if (capture)
        {
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
        }
        if (cwd is not null)
        {
            psi.WorkingDirectory = cwd;
        }
        foreach (var x in a)
        {
            psi.ArgumentList.Add(x);
        }
        psi.Environment["ANIMEJANAI_CONFIG_DIR"] = config;
        psi.Environment["XDG_RUNTIME_DIR"] = RunDir;
        psi.Environment["LD_LIBRARY_PATH"] = CallerLibPath;
        psi.Environment.Remove("ANIMEJANAI_ORIG_LD_LIBRARY_PATH");
        psi.Environment["ANIMEJANAI_LAUNCHER_LOG"] = LogFile;
        if (env is not null)
        {
            foreach (var (k, v) in env)
            {
                if (v is null)
                {
                    psi.Environment.Remove(k);
                }
                else
                {
                    psi.Environment[k] = v;
                }
            }
        }
        var p = Process.Start(psi)!;
        if (!capture)
        {
            started.Add(p);
        }
        return p;
    }

    /// Players on the scratch config: processes with --config-dir=<config> on their command line.
    List<int> Players()
    {
        var pids = new List<int>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out int pid))
            {
                continue;
            }
            try
            {
                if (CommandLine(pid).Contains($"--config-dir={config}") && File.ReadAllText($"/proc/{pid}/stat").Split(' ')[2] != "Z")
                {
                    pids.Add(pid);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return pids;
    }

    static List<string> CommandLine(int pid) =>
        File.ReadAllText($"/proc/{pid}/cmdline").Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();

    static Dictionary<string, string> Environ(int pid) =>
        File.ReadAllText($"/proc/{pid}/environ").Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(kv => kv.Split('=', 2)).Where(kv => kv.Length == 2)
            .GroupBy(kv => kv[0]).ToDictionary(g => g.Key, g => g.Last()[1]);

    void KillPlayers()
    {
        foreach (var pid in Players())
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill();
                p.WaitForExit(5000);
            }
            catch (Exception) { }
        }
        // reap launchers that became players
        foreach (var p in started)
        {
            try { if (p.HasExited) p.WaitForExit(); } catch (Exception) { }
        }
        try { File.Delete(Path.Combine(RunDir, InstanceName + ".last-launch")); } catch (IOException) { }
    }

    static bool WaitFor(Func<bool> condition, int timeoutMs = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }
            Thread.Sleep(25);
        }
        return condition();
    }

    static string? Which(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
}
