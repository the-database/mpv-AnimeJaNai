// AnimeJaNaiUpdater — keeps an installed mpv-upscale-2x_animejanai folder up to date in place,
// preserving user files. Ships at the install root next to mpvnet.exe.
//
//   AnimeJaNaiUpdater.exe --check   prints UPDATE_AVAILABLE <ver> | UP_TO_DATE <ver> (for the lua)
//   AnimeJaNaiUpdater.exe --apply   waits for mpv to close, downloads + applies, relaunches mpv
//
// Update size is tiered: if the latest release's heavy deps match what's installed (compared via
// manifest.json) only the small overlay archive is fetched; otherwise the full package.

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
        default:
            Console.WriteLine("Usage: AnimeJaNaiUpdater.exe [--check|--apply]");
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

record Release(string Tag, List<Asset> Assets);
record Asset(string Name, string Url);
