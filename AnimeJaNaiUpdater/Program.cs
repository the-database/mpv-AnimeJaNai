// AnimeJaNaiUpdater — keeps an installed mpv-upscale-2x_animejanai folder up to date in place,
// preserving user files, and manages hardware-specific component packs ("AnimeJaNai Manager").
// Ships at the install root next to mpvnet.exe.
//
//   AnimeJaNaiUpdater.exe --check        prints UPDATE_AVAILABLE <ver> | UP_TO_DATE <ver> (for the lua)
//   AnimeJaNaiUpdater.exe --apply        waits for mpv to close, downloads + applies, relaunches mpv
//   AnimeJaNaiUpdater.exe --components   detect GPU, list installed/available packs + recommendation
//   AnimeJaNaiUpdater.exe --install X    download + install component pack X
//   AnimeJaNaiUpdater.exe --remove X     delete component pack X's files
//   AnimeJaNaiUpdater.exe --auto         install everything the detected hardware recommends
//
// Update size is tiered: if the latest release's heavy deps match what's installed (compared via
// manifest.json) only the small overlay archive is fetched; otherwise the full package.
//
// Component packs are subsets of the install (TensorRT runtime, per-GPU-generation builder
// resources, RIFE models), emitted by the package builder as component-<name>.7z + packs.json
// release assets. Archives are rooted at the install dir, so extraction is installation;
// packs.json carries each pack's file list, so removal is deletion. Installed state lives in
// components.json at the install root (inferred from disk for installs that predate it).
// Dev override: set ANIMEJANAI_PACKS_DIR to a local directory with packs.json + archives.

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using static Downloader;

const string Repo = "the-database/mpv-upscale-2x_animejanai";
string apiLatest = $"https://api.github.com/repos/{Repo}/releases/latest";

// Single-file exe: BaseDirectory is the folder the exe runs from = the install root.
string installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, '/');

// Platform-specific names come from the install's manifest.json so this same code works on Windows
// and (later) Linux. Fallbacks keep older installs (no manifest fields) working on Windows.
string localManifest = Path.Combine(installDir, "manifest.json");
string playerExe = ReadManifestString(localManifest, "player_executable", "mpvnet.exe");
string archiveTool = ReadManifestString(localManifest, "archive_tool", "7z.exe");

string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "--check";

try
{
    switch (mode)
    {
        case "--check":
            await CheckAsync();
            return 0;
        case "--apply":
            return await ApplyAsync();
        case "--components":
            await ComponentsAsync(null, args.Contains("--json"));
            return 0;
        case "--install":
            return await InstallComponentAsync(args.Length > 1 ? args[1] : "");
        case "--remove":
            return RemoveComponent(args.Length > 1 ? args[1] : "");
        case "--auto":
            return await AutoComponentsAsync();
        case "--recommend":
            return await RecommendAsync();
        default:
            Console.WriteLine("Usage: AnimeJaNaiUpdater.exe [--check|--apply|--components|--install <pack>|--remove <pack>|--auto|--recommend]");
            return 2;
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Updater error: {ex.Message}");
    return 1;
}

// ---- modes ---------------------------------------------------------------------------------

async Task CheckAsync()
{
    // Before the network call, so post-update housekeeping happens even offline.
    CleanupLegacy();
    MigrateUserConf();
    SyncInputConf();
    MigrateAnimeJaNaiConf();
    var release = await GetLatestReleaseAsync();
    string local = ReadLocalVersion();
    if (IsNewer(release.Tag, local))
    {
        Console.WriteLine($"UPDATE_AVAILABLE {release.Tag}");
    }
    else
    {
        Console.WriteLine($"UP_TO_DATE {local}");
    }
}

// 3.3.x -> 3.4.x leftovers: a full update copies the new package over the install but never
// deletes, so the retired VapourSynth/Python runtime (~5.5 GB, mostly vs-plugins) and the old
// in-folder ConfEditor survive the upgrade. The launcher lua runs --check on every mpv start,
// so this fires right after the post-update relaunch and is a cheap no-op afterwards. The gate
// is structural rather than a version compare: a legacy marker AND a 3.4-era marker must both
// exist, which is never true on a real 3.3.x install (no Manager / inference dir) nor on a
// fresh 3.4 one (no VSPipe).
void CleanupLegacy()
{
    try
    {
        bool legacy = File.Exists(Path.Combine(installDir, "VSPipe.exe"));
        bool current = File.Exists(Path.Combine(installDir, "AnimeJaNaiManager.exe")) ||
                       Directory.Exists(Path.Combine(installDir, "animejanai", "inference"));
        if (!legacy || !current)
        {
            return;
        }

        string[] dirs =
        {
            "vs-plugins", "vs-coreplugins", "vs-scripts", "vsgenstubs4", "Lib",
            "__pycache__", Path.Combine("animejanai", "core"),
        };
        // Only files 3.4.0 no longer ships. The mpv.net runtime (Locale/, *_cor3.dll, the
        // msvcp/vcruntime family, MediaInfo.dll), 7z.dll and yt-dlp.exe stay.
        string[] files =
        {
            "VSPipe.exe", "VSScript.dll", "VSVFW.dll", "AVFS.exe",
            "pfm-192-vapoursynth-win.exe", "portable.vs", "vsmlrt.py", "vsrepo.py",
            "vsgenstubs.py", "vspackages3.json", "MANIFEST.in", "7z.exe",
            "python.exe", "pythonw.exe", "python.cat", "python3.dll",
            "python313.dll", "python313._pth", "python313.zip", "sqlite3.dll",
            "libcrypto-3.dll", "libssl-3.dll", "libffi-8.dll",
            Path.Combine("animejanai", "AnimeJaNaiConfEditor.exe"),
            Path.Combine("animejanai", "av_libglesv2.dll"),
            Path.Combine("animejanai", "libHarfBuzzSharp.dll"),
            Path.Combine("animejanai", "libSkiaSharp.dll"),
        };

        long freed = 0;
        foreach (var rel in dirs)
        {
            string p = Path.Combine(installDir, rel);
            if (!Directory.Exists(p))
            {
                continue;
            }
            try
            {
                freed += new DirectoryInfo(p).EnumerateFiles("*", SearchOption.AllDirectories)
                                             .Sum(f => f.Length);
                Directory.Delete(p, true);
            }
            catch { /* locked file etc. - retried on a later start */ }
        }
        var pyds = Directory.EnumerateFiles(installDir, "*.pyd")
                            .Select(p => Path.GetFileName(p)!);
        foreach (var rel in files.Concat(pyds))
        {
            string p = Path.Combine(installDir, rel);
            if (!File.Exists(p))
            {
                continue;
            }
            try
            {
                long len = new FileInfo(p).Length;
                File.SetAttributes(p, FileAttributes.Normal);
                File.Delete(p);
                freed += len;
            }
            catch { /* retried on a later start */ }
        }
        if (freed > 0)
        {
            string amount = freed >= 1073741824
                ? $"{freed / 1073741824.0:F1} GB"
                : $"{freed / 1048576} MB";
            Console.WriteLine(
                $"LEGACY_CLEANUP freed {amount} of retired VapourSynth/Python files");
        }
    }
    catch { /* cleanup must never break --check */ }
}

// One-time retirement of mpv-user.conf. Since 3.4.0 mpv.conf is the user's own
// file (it includes the managed mpv-animejanai.conf), so the old separate
// mpv-user.conf is deprecated and confusing - it could still hold settings
// carried from 3.3.x. Fold its real settings into mpv.conf under the
// "Your settings below" marker, then delete it. Gated on mpv.conf being the
// new include-style file, so a pre-upgrade 3.3.x mpv.conf is left alone; once
// mpv-user.conf is gone this is a no-op. Fresh installs never ship it.
void MigrateUserConf()
{
    try
    {
        string pc = Path.Combine(installDir, "portable_config");
        string userConf = Path.Combine(pc, "mpv-user.conf");
        string mpvConf = Path.Combine(pc, "mpv.conf");
        if (!File.Exists(userConf) || !File.Exists(mpvConf))
        {
            return;
        }
        string mpv = File.ReadAllText(mpvConf);
        // Only fold into the new-style (include-based) mpv.conf; never touch a
        // legacy managed mpv.conf that an update is about to overwrite anyway.
        if (!mpv.Contains("mpv-animejanai.conf"))
        {
            return;
        }
        string nl = mpv.Contains("\r\n") ? "\r\n" : "\n";
        var keepLines = mpv.Replace("\r\n", "\n").Split('\n').ToList();
        // Drop any lingering include of mpv-user.conf (older 3.4.0 builds shipped one).
        keepLines.RemoveAll(l => l.TrimStart().StartsWith("include") &&
                                 l.Contains("mpv-user"));
        // Real settings = non-blank, non-comment lines the user actually added.
        var settings = File.ReadAllLines(userConf)
            .Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith("#"))
            .ToList();
        if (settings.Count > 0)
        {
            keepLines.Add("");
            keepLines.Add("# Migrated from mpv-user.conf (retired in 3.4.0):");
            keepLines.AddRange(settings);
        }
        File.WriteAllText(mpvConf, string.Join(nl, keepLines));
        File.Delete(userConf);
        Console.WriteLine(settings.Count > 0
            ? $"USER_CONF_MIGRATED {settings.Count} setting(s) into mpv.conf"
            : "USER_CONF_RETIRED (no custom settings to migrate)");
    }
    catch { /* migration must never break --check */ }
}

// input.conf is the user's own file, but mpv's input.conf has no include and
// mpv.net builds its menu from input.conf's #menu: annotations - so the managed
// AnimeJaNai keybindings live inside input.conf as a marked block. This refreshes
// that block from input-animejanai.conf (overwritten on update) while preserving
// the user's section below the END marker, and one-time migrates the retired
// input-user.conf into that section. Mirrors the mpv-user.conf retirement.
void SyncInputConf()
{
    try
    {
        string pc = Path.Combine(installDir, "portable_config");
        string inputConf = Path.Combine(pc, "input.conf");
        string managedSrc = Path.Combine(pc, "input-animejanai.conf");
        if (!File.Exists(inputConf) || !File.Exists(managedSrc))
        {
            return;
        }
        string raw = File.ReadAllText(inputConf);
        string nl = raw.Contains("\r\n") ? "\r\n" : "\n";
        var lines = raw.Replace("\r\n", "\n").Split('\n').ToList();
        int begin = lines.FindIndex(l => l.StartsWith("#@ANIMEJANAI-MANAGED-BEGIN"));
        int end = lines.FindIndex(l => l.StartsWith("#@ANIMEJANAI-MANAGED-END"));
        if (begin < 0 || end < 0 || end <= begin)
        {
            return;  // not the managed-block layout (e.g. a pre-3.4 input.conf); leave it
        }

        string currentBlock = string.Join("\n", lines.GetRange(begin + 1, end - begin - 1)).Trim('\n');
        string newBlock = File.ReadAllText(managedSrc).Replace("\r\n", "\n").Trim('\n');
        var userSection = lines.GetRange(end + 1, lines.Count - end - 1);  // after END marker

        // One-time fold of the retired input-user.conf into the user section.
        string userConf = Path.Combine(pc, "input-user.conf");
        bool migrated = false;
        if (File.Exists(userConf))
        {
            var binds = File.ReadAllLines(userConf)
                .Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith("#")).ToList();
            if (binds.Count > 0)
            {
                userSection.Add("");
                userSection.Add("# Migrated from input-user.conf (retired in 3.4.0):");
                userSection.AddRange(binds);
            }
            try { File.Delete(userConf); } catch { }
            migrated = true;
        }
        // The loader script that applied input-user.conf is no longer needed.
        try { File.Delete(Path.Combine(pc, "scripts", "animejanai_userinput.lua")); } catch { }

        if (currentBlock == newBlock && !migrated)
        {
            return;  // block already current and nothing to migrate
        }

        var rebuilt = new List<string>();
        rebuilt.AddRange(lines.GetRange(0, begin + 1));  // header through the BEGIN marker
        rebuilt.AddRange(newBlock.Split('\n'));
        rebuilt.Add(lines[end]);                         // the END marker line
        rebuilt.AddRange(userSection);
        File.WriteAllText(inputConf, string.Join(nl, rebuilt));
        Console.WriteLine(migrated
            ? "INPUT_CONF_SYNCED (managed block refreshed; input-user.conf folded in and retired)"
            : "INPUT_CONF_SYNCED (managed keybindings block refreshed)");
    }
    catch { /* must never break --check */ }
}

// The SD model was re-exported at ONNX opset 21 (the op23 build fell back to the
// CPU on the DirectML backend); the op23 .onnx no longer ships. animejanai.conf
// is user-preserved, so a custom profile that named the old model by hand would
// otherwise error with "model not found" on that slot. Plain idempotent rewrite
// of the one renamed name; no-op once rewritten or if absent. Runs on --check
// (every start), so it fires on the post-update relaunch - with the new binary,
// since the updater ships in overlay_paths - before the user plays.
void MigrateAnimeJaNaiConf()
{
    try
    {
        string conf = Path.Combine(installDir, "animejanai", "animejanai.conf");
        if (!File.Exists(conf))
        {
            return;
        }
        const string oldName =
            "2x_AnimeJaNai_SD_V1beta34_Compact_1x3xHxW_dyn-HW_strong_fp16_op23_dynamo";
        const string newName =
            "2x_AnimeJaNai_SD_V1beta34_Compact_1x3xHxW_dyn-HW_strong_fp16_op21_dynamo";
        string text = File.ReadAllText(conf);
        if (!text.Contains(oldName))
        {
            return;
        }
        File.WriteAllText(conf, text.Replace(oldName, newName));
        Console.WriteLine("ANIMEJANAI_CONF_MIGRATED (SD model op23 -> op21)");
    }
    catch { /* migration must never break --check */ }
}

async Task<int> ApplyAsync()
{
    // The launching lua quits mpv right after starting us; wait for it to release file locks.
    WaitForProcessExit(Path.GetFileNameWithoutExtension(playerExe), TimeSpan.FromSeconds(30));

    var release = await GetLatestReleaseAsync();
    string local = ReadLocalVersion();
    if (!IsNewer(release.Tag, local))
    {
        Console.WriteLine($"Already up to date (v{local}). Relaunching mpv.");
        RelaunchMpv();
        return 0;
    }

    Console.WriteLine($"Updating from v{local} to v{release.Tag}...");
    string work = Path.Combine(Path.GetTempPath(), $"animejanai-update-{release.Tag}");
    Directory.CreateDirectory(work);
    string staging = Path.Combine(work, "staging");

    try
    {
        bool overlay = await IsOverlaySufficientAsync(release, work);
        Console.WriteLine(overlay
            ? "Heavy dependencies unchanged — downloading lightweight overlay update."
            : "Dependencies changed — downloading full package (this is large).");

        string archiveEntry = overlay
            ? await DownloadOverlayAsync(release, work)
            : await DownloadFullAsync(release, work);

        Console.WriteLine("Extracting...");
        Extract(archiveEntry, staging);

        // For the full package the archive root is the versioned folder; the overlay is flat.
        string sourceRoot = overlay ? staging : FindVersionedRoot(staging);

        BackupInputConf(local);
        Console.WriteLine("Applying update (your animejanai.conf, mpv-user.conf and added models are kept)...");
        ApplyOver(sourceRoot, installDir, overlay);

        Console.WriteLine($"Update to v{release.Tag} complete.");
    }
    finally
    {
        TryDelete(() => Directory.Delete(work, true));
    }

    RelaunchMpv();
    return 0;
}

// ---- update decision -----------------------------------------------------------------------

// Overlay is enough when the latest release's heavy deps match the installed manifest's deps.
async Task<bool> IsOverlaySufficientAsync(Release release, string work)
{
    var asset = release.Assets.FirstOrDefault(a => a.Name == "manifest.json");
    if (asset is null)
    {
        return false; // no manifest published -> play safe with a full update
    }

    string remotePath = Path.Combine(work, "manifest.remote.json");
    await DownloadFileAsync(asset.Url, remotePath, _ => { });

    string? localDeps = ReadDepsRaw(Path.Combine(installDir, "manifest.json"));
    string? remoteDeps = ReadDepsRaw(remotePath);
    return localDeps != null && remoteDeps != null && localDeps == remoteDeps;
}

static string ReadManifestString(string manifestPath, string key, string fallback)
{
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            if (!string.IsNullOrEmpty(s)) return s;
        }
    }
    catch { /* missing/unreadable manifest -> fallback */ }
    return fallback;
}

static string? ReadDepsRaw(string manifestPath)
{
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        return doc.RootElement.TryGetProperty("deps", out var deps) ? deps.GetRawText() : null;
    }
    catch
    {
        return null;
    }
}

// ---- downloads -----------------------------------------------------------------------------

async Task<string> DownloadOverlayAsync(Release release, string work)
{
    var asset = release.Assets.FirstOrDefault(a => Regex.IsMatch(a.Name, @"overlay-.*\.7z$"))
        ?? throw new InvalidOperationException("No overlay asset found on the latest release.");
    string dest = Path.Combine(work, asset.Name);
    await DownloadWithProgress(asset, dest);
    return dest;
}

async Task<string> DownloadFullAsync(Release release, string work)
{
    var parts = release.Assets
        .Where(a => a.Name.Contains("full-package-") && Regex.IsMatch(a.Name, @"\.7z\.\d+$"))
        .OrderBy(a => a.Name)
        .ToList();
    if (parts.Count == 0)
    {
        throw new InvalidOperationException("No full-package assets found on the latest release.");
    }

    foreach (var part in parts)
    {
        await DownloadWithProgress(part, Path.Combine(work, part.Name));
    }
    // 7z auto-discovers the remaining volumes from the first part.
    return Path.Combine(work, parts[0].Name);
}

async Task DownloadWithProgress(Asset asset, string dest)
{
    double last = -10;
    Console.WriteLine($"Downloading {asset.Name}...");
    await DownloadFileAsync(asset.Url, dest, p =>
    {
        if (p >= last + 5)
        {
            Console.WriteLine($"  {asset.Name}: {p}%");
            last = p;
        }
    });
}

// ---- extraction / apply --------------------------------------------------------------------

void Extract(string archiveEntry, string outDir)
{
    Directory.CreateDirectory(outDir);
    string sevenZip = Path.Combine(installDir, archiveTool);
    var psi = new ProcessStartInfo
    {
        FileName = sevenZip,
        Arguments = $"x \"{archiveEntry}\" -o\"{outDir}\" -y",
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };
    using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start 7z.exe");
    string err = p.StandardError.ReadToEnd();
    p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode != 0)
    {
        throw new InvalidOperationException($"7z extraction failed (exit {p.ExitCode}): {err}");
    }
}

string FindVersionedRoot(string staging)
{
    var dir = Directory.GetDirectories(staging)
        .FirstOrDefault(d => Path.GetFileName(d).StartsWith("mpv-upscale-2x_animejanai-v"));
    return dir ?? staging;
}

// Copy the freshly-extracted tree over the install. user_preserve paths from the local manifest are
// never overwritten (relevant only to a full update — the overlay archive doesn't contain them).
void ApplyOver(string sourceRoot, string targetRoot, bool overlay)
{
    var preserve = overlay ? new HashSet<string>() : ReadUserPreserve(Path.Combine(targetRoot, "manifest.json"));

    // Self-update: a running exe can't be overwritten, but it can be renamed out of the way first.
    // Derive the name from the running process so this works whether it's AnimeJaNaiUpdater.exe
    // (Windows) or AnimeJaNaiUpdater (Linux).
    string ownExe = Environment.ProcessPath ?? "";
    string ownName = string.IsNullOrEmpty(ownExe) ? "" : Path.GetFileName(ownExe);
    string newUpdater = Path.Combine(sourceRoot, ownName);
    if (!string.IsNullOrEmpty(ownExe) && File.Exists(newUpdater) &&
        Path.GetFullPath(ownExe).Equals(Path.GetFullPath(Path.Combine(targetRoot, ownName)), StringComparison.OrdinalIgnoreCase))
    {
        string old = ownExe + ".old";
        TryDelete(() => File.Delete(old));
        TryDelete(() => File.Move(ownExe, old));
    }

    CopyTree(sourceRoot, targetRoot, sourceRoot, preserve);
}

void CopyTree(string srcDir, string dstDir, string sourceRoot, HashSet<string> preserve)
{
    Directory.CreateDirectory(dstDir);

    foreach (var file in Directory.GetFiles(srcDir))
    {
        string rel = NormalizeRel(Path.GetRelativePath(sourceRoot, file));
        if (IsPreserved(rel, preserve))
        {
            continue;
        }
        string dst = Path.Combine(dstDir, Path.GetFileName(file));
        TryDelete(() => { if (File.Exists(dst)) File.SetAttributes(dst, FileAttributes.Normal); });
        File.Copy(file, dst, true);
    }

    foreach (var sub in Directory.GetDirectories(srcDir))
    {
        string rel = NormalizeRel(Path.GetRelativePath(sourceRoot, sub));
        if (IsPreserved(rel, preserve))
        {
            continue;
        }
        CopyTree(sub, Path.Combine(dstDir, Path.GetFileName(sub)), sourceRoot, preserve);
    }
}

static bool IsPreserved(string rel, HashSet<string> preserve)
{
    foreach (var p in preserve)
    {
        if (rel == p || rel.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
    }
    return false;
}

static string NormalizeRel(string rel) => rel.Replace('\\', '/');

static HashSet<string> ReadUserPreserve(string manifestPath)
{
    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (doc.RootElement.TryGetProperty("user_preserve", out var arr))
        {
            foreach (var e in arr.EnumerateArray())
            {
                var v = e.GetString();
                if (!string.IsNullOrEmpty(v)) set.Add(NormalizeRel(v));
            }
        }
    }
    catch { /* if unreadable, preserve nothing extra — overlay path is unaffected */ }
    return set;
}

void BackupInputConf(string oldVersion)
{
    string input = Path.Combine(installDir, "portable_config", "input.conf");
    if (File.Exists(input))
    {
        string bak = Path.Combine(installDir, "portable_config", $"input.conf.bak-{oldVersion}");
        TryDelete(() => File.Delete(bak));
        try { File.Copy(input, bak, true); }
        catch (Exception e) { Console.WriteLine($"  (could not back up input.conf: {e.Message})"); }
    }
}

// ---- helpers -------------------------------------------------------------------------------

string ReadLocalVersion()
{
    string path = Path.Combine(installDir, "version.txt");
    return File.Exists(path) ? File.ReadAllText(path).Trim() : "0.0.0";
}

async Task<Release> GetLatestReleaseAsync()
{
    using var client = new HttpClient();
    client.DefaultRequestHeaders.UserAgent.ParseAdd("AnimeJaNaiUpdater");
    string json = await client.GetStringAsync(apiLatest);
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;
    string tag = root.GetProperty("tag_name").GetString() ?? "";
    var assets = new List<Asset>();
    if (root.TryGetProperty("assets", out var arr))
    {
        foreach (var a in arr.EnumerateArray())
        {
            assets.Add(new Asset(
                a.GetProperty("name").GetString() ?? "",
                a.GetProperty("browser_download_url").GetString() ?? ""));
        }
    }
    return new Release(tag, assets);
}

void RelaunchMpv()
{
    string mpv = Path.Combine(installDir, playerExe);
    if (File.Exists(mpv))
    {
        try { Process.Start(new ProcessStartInfo { FileName = mpv, UseShellExecute = true }); }
        catch (Exception e) { Console.WriteLine($"Could not relaunch mpv: {e.Message}"); }
    }
}

static void WaitForProcessExit(string name, TimeSpan timeout)
{
    var sw = Stopwatch.StartNew();
    while (sw.Elapsed < timeout && Process.GetProcessesByName(name).Length > 0)
    {
        Thread.Sleep(500);
    }
}

static void TryDelete(Action action)
{
    try { action(); } catch { /* best effort */ }
}

// Numeric-dotted semver compare; "3.2.10" > "3.2.9". Falls back to ordinal on non-numeric parts.
static bool IsNewer(string remote, string local)
{
    int[] R = Parse(remote), L = Parse(local);
    for (int i = 0; i < Math.Max(R.Length, L.Length); i++)
    {
        int r = i < R.Length ? R[i] : 0;
        int l = i < L.Length ? L[i] : 0;
        if (r != l) return r > l;
    }
    return false;

    static int[] Parse(string v)
    {
        var parts = v.TrimStart('v', 'V').Split('.');
        var nums = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            nums[i] = int.TryParse(new string(parts[i].TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0;
        }
        return nums;
    }
}

// ---- component manager ----------------------------------------------------------------------

async Task<PackIndex> GetPackIndexAsync()
{
    string? local = Environment.GetEnvironmentVariable("ANIMEJANAI_PACKS_DIR");
    string json;
    List<Asset> assets;
    if (!string.IsNullOrEmpty(local))
    {
        json = File.ReadAllText(Path.Combine(local, "packs.json"));
        assets = Directory.GetFiles(local, "component-*.7z")
            .Select(f => new Asset(Path.GetFileName(f), f)).ToList();
    }
    else
    {
        var release = await GetLatestReleaseAsync();
        var idx = release.Assets.FirstOrDefault(a => a.Name == "packs.json")
            ?? throw new InvalidOperationException(
                "The latest release publishes no component packs (packs.json missing).");
        using var client = NewClient();
        json = await client.GetStringAsync(idx.Url);
        assets = release.Assets;
    }

    var packs = new List<Pack>();
    using var doc = JsonDocument.Parse(json);
    foreach (var e in doc.RootElement.GetProperty("packs").EnumerateArray())
    {
        var files = e.GetProperty("files").EnumerateArray()
            .Select(f => f.GetString() ?? "").Where(f => f.Length > 0).ToList();
        string asset = e.GetProperty("asset").GetString() ?? "";
        packs.Add(new Pack(
            e.GetProperty("name").GetString() ?? "",
            asset,
            assets.FirstOrDefault(a => a.Name == asset)?.Url,
            e.GetProperty("bytes").GetInt64(),
            files));
    }
    return new PackIndex(
        doc.RootElement.TryGetProperty("package_version", out var v)
            ? v.GetString() ?? "" : "", packs);
}

// Installed state: components.json, else inferred from what's on disk so
// full installs that predate the manager work out of the box.
Dictionary<string, string> ReadInstalledComponents(PackIndex index)
{
    string path = Path.Combine(installDir, "components.json");
    var installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (File.Exists(path))
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var e in doc.RootElement.GetProperty("installed").EnumerateObject())
            {
                installed[e.Name] = e.Value.GetString() ?? "";
            }
            return installed;
        }
        catch { /* fall through to inference */ }
    }
    foreach (var pack in index.Packs)
    {
        // a pack counts as installed when all its files exist
        if (pack.Files.Count > 0 &&
            pack.Files.All(f => File.Exists(Path.Combine(installDir, f))))
        {
            installed[pack.Name] = "(pre-manager install)";
        }
    }
    return installed;
}

void WriteInstalledComponents(Dictionary<string, string> installed)
{
    File.WriteAllText(Path.Combine(installDir, "components.json"),
        JsonSerializer.Serialize(new { installed },
            new JsonSerializerOptions { WriteIndented = true }));
}

string? PackVersionMismatch(PackIndex index)
{
    string localVersion = ReadManifestString(localManifest, "package_version", "");
    return localVersion != "" && index.PackageVersion != "" && localVersion != index.PackageVersion
        ? $"Installed package is v{localVersion} but the published packs are for v{index.PackageVersion}."
        : null;
}

// NVIDIA detection via NVML (ships with the driver); its absence means a
// non-NVIDIA GPU, which is exactly the DirectML recommendation.
static (bool HasNvidia, string Sm, string GpuName) DetectGpu()
{
    try
    {
        if (Nvml.nvmlInit_v2() != 0)
        {
            return (false, "", "");
        }
        try
        {
            if (Nvml.nvmlDeviceGetHandleByIndex_v2(0, out var dev) != 0)
            {
                return (false, "", "");
            }
            Nvml.nvmlDeviceGetCudaComputeCapability(dev, out int major, out int minor);
            var name = new byte[96];
            Nvml.nvmlDeviceGetName(dev, name, (uint)name.Length);
            string gpu = System.Text.Encoding.ASCII.GetString(name).TrimEnd('\0');
            return (true, $"sm{major}{minor}", gpu);
        }
        finally { Nvml.nvmlShutdown(); }
    }
    catch
    {
        return (false, "", "");
    }
}

// Hardware-matched packs only. RIFE is a user choice, not a recommendation:
// it is preselected on installs that have never managed components (fresh or
// legacy-full), and once the user has made any component decision their
// choice stands - no nagging after a deliberate removal.
List<string> RecommendedPacks(PackIndex index, bool hasNvidia, string sm)
{
    var rec = new List<string>();
    if (hasNvidia)
    {
        rec.Add("trt-runtime");
        // exact generation pack if published, else the PTX fallback pack
        // (JIT-compiles for newer GPUs than this TensorRT knows)
        rec.Add(index.Packs.Any(p => p.Name == $"trt-{sm}") ? $"trt-{sm}" : "trt-ptx");
    }
    return rec;
}

bool ComponentsNeverManaged() => !File.Exists(Path.Combine(installDir, "components.json"));

bool PreselectPack(Pack pack, Dictionary<string, string> installed, List<string> rec) =>
    installed.ContainsKey(pack.Name) || rec.Contains(pack.Name) ||
    (pack.Name == "rife" && ComponentsNeverManaged());

async Task ComponentsAsync(PackIndex? prefetched, bool json = false)
{
    var index = prefetched ?? await GetPackIndexAsync();
    var installed = ReadInstalledComponents(index);
    var (hasNvidia, sm, gpu) = DetectGpu();
    var rec = RecommendedPacks(index, hasNvidia, sm);
    if (json)
    {
        // consumed by the AnimeJaNai Manager GUI; keep keys stable
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            package_version = index.PackageVersion,
            version_mismatch = PackVersionMismatch(index),
            gpu = new { nvidia = hasNvidia, sm, name = gpu },
            packs = index.Packs.Select(p => new
            {
                name = p.Name,
                bytes = p.Bytes,
                installed = installed.ContainsKey(p.Name),
                recommended = rec.Contains(p.Name),
                preselect = PreselectPack(p, installed, rec),
            }),
        }));
        return;
    }
    if (PackVersionMismatch(index) is string warn)
    {
        Console.WriteLine(warn);
        Console.WriteLine();
    }
    Console.WriteLine(hasNvidia
        ? $"GPU: {gpu} ({sm}) - TensorRT recommended"
        : "GPU: no NVIDIA device detected - DirectML (in the core install) covers AMD/Intel");
    Console.WriteLine($"Recommended packs: {string.Join(", ", rec)}");
    Console.WriteLine();
    foreach (var pack in index.Packs)
    {
        string state = installed.ContainsKey(pack.Name) ? "installed" :
                       rec.Contains(pack.Name) ? "RECOMMENDED" :
                       PreselectPack(pack, installed, rec) ? "default on new installs" :
                       "available";
        Console.WriteLine($"  {pack.Name,-14} {pack.Bytes / 1048576,6} MB  {state}");
    }
}

async Task<int> InstallComponentAsync(string name)
{
    if (string.IsNullOrEmpty(name))
    {
        Console.WriteLine("--install needs a pack name (see --components)");
        return 2;
    }
    var index = await GetPackIndexAsync();
    var pack = index.Packs.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    if (pack is null)
    {
        Console.WriteLine($"Unknown pack '{name}'. Available: {string.Join(", ", index.Packs.Select(p => p.Name))}");
        return 2;
    }
    if (pack.Url is null)
    {
        Console.WriteLine($"Pack '{name}' has no downloadable asset on the latest release.");
        return 1;
    }
    if (PackVersionMismatch(index) is string warn)
    {
        // a pack from a different release can mismatch the installed aji/TensorRT builds
        Console.WriteLine(warn);
        Console.WriteLine("Update first (AnimeJaNaiUpdater.exe --apply), then install components.");
        return 1;
    }

    string work = Path.Combine(Path.GetTempPath(), "animejanai-packs");
    Directory.CreateDirectory(work);
    string archive = Path.Combine(work, pack.Asset);
    if (pack.Url.Contains("://"))
    {
        await DownloadWithProgress(new Asset(pack.Asset, pack.Url), archive);
    }
    else
    {
        File.Copy(pack.Url, archive, true); // ANIMEJANAI_PACKS_DIR dev path
    }
    Console.WriteLine($"Installing {pack.Name}...");
    Extract(archive, installDir);
    TryDelete(() => File.Delete(archive));

    var installed = ReadInstalledComponents(index);
    installed[pack.Name] = index.PackageVersion;
    WriteInstalledComponents(installed);
    Console.WriteLine($"{pack.Name} installed.");
    return 0;
}

int RemoveComponent(string name)
{
    var index = GetPackIndexAsync().GetAwaiter().GetResult();
    var pack = index.Packs.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    if (pack is null)
    {
        Console.WriteLine($"Unknown pack '{name}'. Available: {string.Join(", ", index.Packs.Select(p => p.Name))}");
        return 2;
    }
    int gone = 0;
    foreach (var f in pack.Files)
    {
        string abs = Path.Combine(installDir, f);
        if (File.Exists(abs))
        {
            TryDelete(() => { File.SetAttributes(abs, FileAttributes.Normal); File.Delete(abs); });
            gone++;
        }
    }
    var installed = ReadInstalledComponents(index);
    installed.Remove(pack.Name);
    WriteInstalledComponents(installed);
    Console.WriteLine($"{pack.Name} removed ({gone} files). Engine caches and models you added are untouched.");
    return 0;
}

async Task<int> AutoComponentsAsync()
{
    var index = await GetPackIndexAsync();
    var (hasNvidia, sm, gpu) = DetectGpu();
    var rec = RecommendedPacks(index, hasNvidia, sm);
    var installed = ReadInstalledComponents(index);
    var missing = index.Packs
        .Where(p => PreselectPack(p, installed, rec) && !installed.ContainsKey(p.Name))
        .Select(p => p.Name).ToList();
    if (missing.Count == 0)
    {
        Console.WriteLine("Everything the detected hardware needs is already installed.");
        await ComponentsAsync(index, false);
        return 0;
    }
    Console.WriteLine($"Installing for {(hasNvidia ? gpu : "DirectML-class GPU")}: {string.Join(", ", missing)}");
    foreach (var name in missing)
    {
        int rc = await InstallComponentAsync(name);
        if (rc != 0)
        {
            return rc;
        }
    }
    return 0;
}

// Hardware summary for the setup installer's component picker, as trivially
// parseable KEY=value lines (Inno Setup has no JSON parser). GPU identity is
// local NVML and always available; TRT_PACKS needs the pack index (network or
// ANIMEJANAI_PACKS_DIR) - if that fails it is emitted empty and the installer
// falls back to a generic TensorRT toggle.
async Task<int> RecommendAsync()
{
    var (hasNvidia, sm, gpu) = DetectGpu();
    Console.WriteLine($"NVIDIA={(hasNvidia ? 1 : 0)}");
    Console.WriteLine($"GPU={gpu}");
    string trtPacks = "";
    string rife = "rife";
    try
    {
        var index = await GetPackIndexAsync();
        trtPacks = string.Join(",", RecommendedPacks(index, hasNvidia, sm));
        if (!index.Packs.Any(p => p.Name == "rife"))
        {
            rife = "";
        }
    }
    catch
    {
        // offline / no published packs yet: GPU lines still printed; the
        // installer keeps the TensorRT toggle and resolves packs at install
        // time (when network is needed anyway to download them).
    }
    Console.WriteLine($"TRT_PACKS={trtPacks}");
    Console.WriteLine($"RIFE={rife}");
    return 0;
}

static HttpClient NewClient()
{
    var client = new HttpClient();
    client.DefaultRequestHeaders.UserAgent.ParseAdd("AnimeJaNaiUpdater");
    return client;
}

static class Nvml
{
    [System.Runtime.InteropServices.DllImport("nvml.dll")]
    public static extern int nvmlInit_v2();
    [System.Runtime.InteropServices.DllImport("nvml.dll")]
    public static extern int nvmlShutdown();
    [System.Runtime.InteropServices.DllImport("nvml.dll")]
    public static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [System.Runtime.InteropServices.DllImport("nvml.dll")]
    public static extern int nvmlDeviceGetCudaComputeCapability(IntPtr device, out int major, out int minor);
    [System.Runtime.InteropServices.DllImport("nvml.dll")]
    public static extern int nvmlDeviceGetName(IntPtr device, byte[] name, uint length);
}

record Release(string Tag, List<Asset> Assets);
record Asset(string Name, string Url);
record Pack(string Name, string Asset, string? Url, long Bytes, List<string> Files);
record PackIndex(string PackageVersion, List<Pack> Packs);
