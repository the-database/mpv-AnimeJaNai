// The mpv.net -> launcher migration (AnimeJaNaiUpdater --check, MigrateFromMpvNetAsync) on a copy
// of a real release, with a store of its own. Windows.
//
//   dotnet run --project tests/FrontendMigration -- <release tree> <new updater exe> <launcher dir> [--seed-store <store>]
//
// <release tree>: an extracted mpv.net-era package (3.12.x). Its own AnimeJaNaiUpdater installs
// TensorRT first, the way an existing zip copy has it (a per-copy store ref naming mpvnet.exe);
// then the new updater, launcher and manifest are put in, as the switching update does.
// --seed-store: an existing pack store whose packs are hard-linked in (nothing downloaded);
// without it aji-runtime downloads the TensorRT runtime pack.
// The store (AJI_RUNTIME_STORE), registry (ANIMEJANAI_TEST_HKCU) and shortcut folders
// (ANIMEJANAI_TEST_SHELL_DIRS) are all scratch: nothing of the user's is read or changed.
// Large files are hard links; anything the updater may rewrite is a real copy, so the release
// tree itself is never modified.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Win32;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: dotnet run --project tests/FrontendMigration -- <release tree> <new updater exe> <launcher dir> [--seed-store <store>]");
    return 2;
}
string tree = Path.GetFullPath(args[0]);
string newUpdater = Path.GetFullPath(args[1]);
string launcherDir = Path.GetFullPath(args[2]);
int seedAt = Array.IndexOf(args, "--seed-store");
string? seed = seedAt >= 0 && seedAt + 1 < args.Length ? Path.GetFullPath(args[seedAt + 1]) : null;

string work = Path.Combine(Path.GetTempPath(), "ajn-migration-" + Guid.NewGuid().ToString("N")[..8]);
string root = Path.Combine(work, "install");
string store = Path.Combine(work, "store");
string shells = Path.Combine(work, "shortcuts");
string hkcu = @"Software\AnimeJaNaiMigrationTest-" + Guid.NewGuid().ToString("N")[..8];
string oldExe = Path.Combine(root, "mpvnet.exe");
string newExe = Path.Combine(root, "mpv-animejanai.exe");
int passed = 0, failed = 0;

void Check(bool ok, string what)
{
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}");
    if (ok) passed++; else failed++;
}

(int Code, string Out) Run(string exe, params string[] a)
{
    var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
    foreach (var x in a) psi.ArgumentList.Add(x);
    psi.Environment["AJI_RUNTIME_STORE"] = store;
    psi.Environment["ANIMEJANAI_TEST_HKCU"] = hkcu;
    psi.Environment["ANIMEJANAI_TEST_SHELL_DIRS"] = shells;
    using var p = Process.Start(psi)!;
    var err = p.StandardError.ReadToEndAsync();
    string output = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    return (p.ExitCode, output + err.Result);
}

JsonNode? Show(string appPath)
{
    var (code, output) = Run(Path.Combine(root, "aji-runtime.exe"), "show", "--app-id", "mpv-animejanai", "--per-copy", "--app-path", appPath);
    if (code != 0) return null;
    var node = JsonNode.Parse(output);
    return node?["has_ref"]?.GetValue<bool>() == true ? node : null;
}

void SetManifest(Action<JsonObject> edit)
{
    string path = Path.Combine(root, "manifest.json");
    var m = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    edit(m);
    File.WriteAllText(path, m.ToJsonString(new() { WriteIndented = true }));
}

try
{
    Console.WriteLine("== setting up a zip copy with TensorRT, as mpv.net-era releases leave it");
    CopyTree(tree, root);
    Directory.CreateDirectory(shells);
    Directory.CreateDirectory(store);
    if (seed is not null)
    {
        foreach (var dir in Directory.GetDirectories(Path.Combine(seed, "packs"), "trt-runtime"))
        {
            LinkDir(dir, Path.Combine(store, "packs", Path.GetFileName(dir)));
        }
    }
    var (rc, output) = Run(Path.Combine(root, "AnimeJaNaiUpdater.exe"), "--install", "trt-runtime");
    Check(rc == 0, $"old updater installs trt-runtime (exit {rc})");
    Check(Show(oldExe) is not null, "store ref names mpvnet.exe");

    Console.WriteLine("== the switching update: new updater, launcher, manifest");
    File.Delete(Path.Combine(root, "AnimeJaNaiUpdater.exe"));
    File.Copy(newUpdater, Path.Combine(root, "AnimeJaNaiUpdater.exe"));
    File.Copy(Path.Combine(launcherDir, "mpv-animejanai.exe"), newExe, true);
    File.Copy(Path.Combine(launcherDir, "mpv-animejanai.com"), Path.Combine(root, "mpv-animejanai.com"), true);
    string indexUrl = "";
    SetManifest(m =>
    {
        m["player_executable"] = "mpv.exe";
        m["player_launcher"] = "mpv-animejanai.exe";
        m["wait_processes"] = new JsonArray("mpv.exe", "mpv.com", "mpvnet.exe", "mpv-animejanai.com");
        indexUrl = m["runtime"]!["index_url"]!.GetValue<string>();
        m["runtime"]!["index_url"] = "https://127.0.0.1:9/unreachable-packs.json";
    });

    Console.WriteLine("== first start offline: nothing may change");
    (rc, output) = Run(Path.Combine(root, "AnimeJaNaiUpdater.exe"), "--check");
    Check(!output.Contains("PLAYER_MIGRATED"), $"no PLAYER_MIGRATED offline ({output.Trim().Split('\n').FirstOrDefault(l => l.Contains("migration"))?.Trim()})");
    Check(!File.Exists(Path.Combine(root, "animejanai", ".frontend-migrated")), "no marker written");
    Check(Show(oldExe) is not null, "old ref still there");
    Check(Show(newExe) is null, "no ref for the launcher yet");

    Console.WriteLine("== next start online");
    SetManifest(m => m["runtime"]!["index_url"] = indexUrl);
    (rc, output) = Run(Path.Combine(root, "AnimeJaNaiUpdater.exe"), "--check");
    Check(output.Contains("PLAYER_MIGRATED"), "--check prints PLAYER_MIGRATED");
    Check(File.Exists(Path.Combine(root, "animejanai", ".frontend-migrated")), "marker written");
    var newRef = Show(newExe);
    Check(newRef?["packs"]?.AsArray().Any(p => p?["name"]?.GetValue<string>() == "trt-runtime" && p["installed"]?.GetValue<bool>() == true) == true, "launcher's ref holds trt-runtime");
    Check(Show(oldExe) is null, "mpvnet.exe's ref dropped");
    Check(!output.Contains("Downloading", StringComparison.OrdinalIgnoreCase), "nothing downloaded");
    (rc, output) = Run(Path.Combine(root, "AnimeJaNaiUpdater.exe"), "--components", "--json");
    Check(System.Text.RegularExpressions.Regex.IsMatch(output, "\"name\":\"trt-runtime\"[^}]*\"installed\":true"),
        "Manager's component list shows TensorRT installed");
    (rc, output) = Run(Path.Combine(root, "aji-runtime.exe"), "gc");
    Check(rc == 0 && Directory.Exists(Path.Combine(store, "packs", "trt-runtime")), "a store gc afterwards frees nothing in use");
    Check(File.Exists(oldExe), "mpvnet.exe is left in place");

    Console.WriteLine("== later starts");
    (rc, output) = Run(Path.Combine(root, "AnimeJaNaiUpdater.exe"), "--check");
    Check(!output.Contains("PLAYER_MIGRATED"), "runs once");
}
finally
{
    Run(Path.Combine(root, "aji-runtime.exe"), "unref", "--app-id", "mpv-animejanai", "--per-copy", "--app-path", newExe);
    Registry.CurrentUser.DeleteSubKeyTree(hkcu, throwOnMissingSubKey: false);
    try { Directory.Delete(work, true); } catch (Exception e) { Console.WriteLine($"(left {work}: {e.Message})"); }
}
Console.WriteLine($"\n{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

// Large binaries as hard links; everything else (configs, manifests, scripts) as real copies,
// since the updater rewrites some of them in place.
static void CopyTree(string from, string to)
{
    foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
    {
        Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
    }
    Directory.CreateDirectory(to);
    foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
    {
        string dest = Path.Combine(to, Path.GetRelativePath(from, file));
        string ext = Path.GetExtension(file).ToLowerInvariant();
        bool link = new FileInfo(file).Length > 1 << 20 && ext is ".dll" or ".exe" or ".onnx" or ".bin" or ".pdb";
        if (!link || !CreateHardLinkW(dest, file, 0))
        {
            File.Copy(file, dest);
        }
    }
}

static void LinkDir(string from, string to)
{
    foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
    {
        Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
    }
    Directory.CreateDirectory(to);
    foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
    {
        string dest = Path.Combine(to, Path.GetRelativePath(from, file));
        if (!CreateHardLinkW(dest, file, 0))
        {
            File.Copy(file, dest);
        }
    }
}

[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
static extern bool CreateHardLinkW(string link, string existing, nint security);
