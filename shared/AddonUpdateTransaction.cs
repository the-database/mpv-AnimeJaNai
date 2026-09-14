using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AnimeJaNai.Addons.Management;

namespace AnimeJaNai.Updates;

// The journal and backup are written before the first installed file changes.
// Startup remains disabled after an interruption, until recovery or commit.
public static class AddonUpdateTransaction
{
    public sealed record Entry(string Path, bool Existed, string? Sha256, bool Remove = false);
    public sealed record Journal(int SchemaVersion, string State, Entry[] Files);
    public const string PackageManifest = "addon-package.json";

    private static string Child(string root, string relative)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string path = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Update path is outside the installation.");
        for (string? p = path; p is not null; p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Updates cannot use links or junctions.");
        return path;
    }
    private static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }
    private static void CopyDurable(string source, string destination)
    {
        using var input = File.OpenRead(source);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output); output.Flush(true);
    }
    private static IEnumerable<string> FilesIn(string root)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(root))
        {
            _ = Child(root, Path.GetFileName(entry)); // Check before descending.
            if (Directory.Exists(entry))
                foreach (string file in FilesIn(entry)) yield return file;
            else yield return entry;
        }
    }
    private static void Write(string path, object value)
    {
        string temporary = path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, value); file.Flush(true); }
        File.Move(temporary, path, true);
    }
    private static FileStream Lock(string root)
    {
        Directory.CreateDirectory(root);
        return new(Child(root, ".ajn-update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public static void ValidatePackage(string source, string target)
    {
        string manifest = Child(source, PackageManifest);
        if (!File.Exists(manifest))
        {
            if (File.Exists(Child(target, PackageManifest)))
                throw new IOException("This update does not include addon support. Use an addon-enabled AnimeJaNai build.");
            return;
        }
        if (new FileInfo(manifest).Length > 1024 * 1024) throw new IOException("Addon package inventory is too large.");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(manifest));
        var record = doc.RootElement;
        if (record.GetProperty("schemaVersion").GetInt32() != 1 || record.GetProperty("platform").GetString() != "win-x64")
            throw new IOException("Unsupported addon package inventory.");
        var files = record.GetProperty("files");
        string[] required = ["addon-host/ajn-addon.exe", "addon-host/ajn-addon-launcher.exe", "addon-host/runtime/wasmtime.exe",
            "addon-host/native-capabilities.json", "AnimeJaNaiManager.exe", "libmpv-2.dll", "mpv.exe"];
        foreach (string name in required)
            if (!files.TryGetProperty(name, out _)) throw new IOException("Addon package is incomplete: " + name);
        foreach (var file in files.EnumerateObject())
        {
            string path = Child(source, file.Name);
            if (!File.Exists(path)) path = Child(target, file.Name); // Unchanged overlay dependency.
            string? expected = file.Value.GetString();
            if (expected?.Length != 64 || Hash(path) != expected) throw new IOException("Addon package file does not match its inventory: " + file.Name);
        }
    }

    private static IEnumerable<Process> Owned(string root, params string[] names)
    {
        var expected = names.Select(n => Path.GetFullPath(Path.Combine(root, n))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names.Select(n => Path.GetFileNameWithoutExtension(n)!).Distinct())
            foreach (var process in Process.GetProcessesByName(name))
            {
                bool match = false;
                try { match = process.Id != Environment.ProcessId && expected.Contains(process.MainModule?.FileName ?? ""); }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                if (match) yield return process; else process.Dispose();
            }
    }

    private static async Task<FileStream> DrainAsync(string root, CancellationToken token)
    {
        // Give current hosts time to cancel jobs and drain their resource owners.
        // Legacy previews have no maintenance monitor: only their exact installed
        // executable paths may be stopped, never an unrelated portable install.
        var timer = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            using var processes = new ProcessSet(Owned(root, "addon-host/ajn-addon.exe", "addon-host/ajn-addon-launcher.exe"));
            if (timer.Elapsed > TimeSpan.FromSeconds(8))
                foreach (var process in processes.Values)
                    try { process.Kill(true); } catch (InvalidOperationException) { }
            if (processes.Values.Count == 0)
            {
                try { return new(InstallationActivity.LockPath(root), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) when (timer.Elapsed < TimeSpan.FromSeconds(20)) { }
            }
            if (timer.Elapsed > TimeSpan.FromSeconds(20)) throw new IOException("Addon processes have not released the installation. Close AnimeJaNai and retry recovery.");
            await Task.Delay(100, token);
        }
    }
    private sealed class ProcessSet(IEnumerable<Process> processes) : IDisposable
    {
        public List<Process> Values { get; } = processes.ToList();
        public void Dispose() { foreach (var process in Values) process.Dispose(); }
    }
    private static void RequireAppsClosed(string root, bool includeManager = true)
    {
        using var apps = new ProcessSet(Owned(root, includeManager ? ["mpv.exe", "mpvnet.exe", "AnimeJaNaiManager.exe"] : ["mpv.exe", "mpvnet.exe"]));
        if (apps.Values.Count > 0) throw new IOException(includeManager
            ? "Close the player and AnimeJaNai Manager before applying or recovering this update."
            : "Close the player before changing component packs. You can keep Manager open.");
    }
    private static void Intent(string root)
    {
        string directory = Child(root, InstallationActivity.DirectoryName);
        Directory.CreateDirectory(directory);
        Write(Child(directory, "pending"), new { schemaVersion = 1, processId = Environment.ProcessId });
    }

    public static async Task ApplyAsync(string source, string target, ISet<string> preserve,
        CancellationToken token = default, Action<int>? afterPublish = null, bool component = false, IEnumerable<string>? removals = null)
    {
        target = Path.GetFullPath(target); source = Path.GetFullPath(source);
        if (target.Equals(source, StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith(Path.TrimEndingDirectorySeparator(source) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith(Path.TrimEndingDirectorySeparator(target) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Update staging and installation must be separate directories.");
        using var update = Lock(target);
        RequireAppsClosed(target, includeManager: !component);
        if (!component) ValidatePackage(source, target);
        Intent(target);
        using var drained = await DrainAsync(target, token);
        RecoverCore(target);
        Intent(target);
        string transaction = Child(target, InstallationActivity.DirectoryName);
        string staged = Child(transaction, "staged"), backup = Child(transaction, "backup");
        var entries = new List<Entry>();
        foreach (string file in FilesIn(source))
        {
            token.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            if (component) RequireComponentPath(relative);
            if (relative.StartsWith(".ajn-", StringComparison.OrdinalIgnoreCase)) continue;
            if (relative.StartsWith("animejanai/addons/", StringComparison.OrdinalIgnoreCase)) continue;
            if (preserve.Any(p => (relative.Equals(p, StringComparison.OrdinalIgnoreCase) || relative.StartsWith(p.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))) &&
                File.Exists(Child(target, relative))) continue;
            string destination = Child(target, relative), prepared = Child(staged, relative), old = Child(backup, relative);
            _ = Child(source, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(prepared)!); CopyDurable(file, prepared);
            string hash = Hash(prepared);
            if (Hash(file) != hash) throw new IOException("Update source changed during staging.");
            bool existed = File.Exists(destination);
            if (existed) { Directory.CreateDirectory(Path.GetDirectoryName(old)!); CopyDurable(destination, old); }
            entries.Add(new(relative, existed, existed ? Hash(old) : null));
        }
        foreach (string relative in removals ?? [])
        {
            if (!component) throw new IOException("Only component transactions can remove listed component files.");
            RequireComponentPath(relative);
            string destination = Child(target, relative), old = Child(backup, relative);
            if (entries.Any(e => e.Path.Equals(relative, StringComparison.OrdinalIgnoreCase))) throw new IOException("Duplicate component path.");
            bool existed = File.Exists(destination);
            if (existed) { Directory.CreateDirectory(Path.GetDirectoryName(old)!); CopyDurable(destination, old); }
            entries.Add(new(relative, existed, existed ? Hash(old) : null, Remove: true));
        }
        Write(Child(transaction, "journal.json"), new Journal(1, "prepared", entries.ToArray()));
        int count = 0;
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            string destination = Child(target, entry.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            // Windows permits renaming this running updater but not overwriting
            // it. Its already-flushed backup is independent of this image file.
            if (destination.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) && File.Exists(destination))
                File.Move(destination, Child(transaction, "running-updater.exe"), true);
            if (entry.Remove) File.Delete(destination);
            else File.Move(Child(staged, entry.Path), destination, true);
            afterPublish?.Invoke(++count);
        }
        Write(Child(transaction, "journal.json"), new Journal(1, "committed", entries.ToArray()));
        File.Delete(Child(transaction, "pending"));
        Cleanup(transaction); // A running updater image may remain until the next run.
    }
    private static void RequireComponentPath(string path)
    {
        if (path.Contains('\\') || path.Contains(':') || path.Split('/').Any(p => p is "." or ".." or ""))
            throw new IOException("Component paths must be normalized relative file paths.");
        if (path != "components.json" && !path.StartsWith("animejanai/inference/", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("animejanai/rife/", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Component transactions may change only inference/runtime and RIFE component files.");
    }

    public static async Task RecoverAsync(string root, CancellationToken token = default)
    {
        root = Path.GetFullPath(root);
        using var update = Lock(root);
        RequireAppsClosed(root);
        Intent(root);
        using var drained = await DrainAsync(root, token);
        RecoverCore(root);
    }
    public static async Task PrepareUninstallAsync(string root, CancellationToken token = default)
    {
        using var update = Lock(root);
        RequireAppsClosed(root);
        Intent(root);
        using var drained = await DrainAsync(root, token);
        // Leave intent in place until the uninstaller removes its own app tree.
        // No external addon data root is traversed or deleted by this operation.
    }
    private static void RecoverCore(string root)
    {
        string transaction = Child(root, InstallationActivity.DirectoryName), journalPath = Child(transaction, "journal.json");
        if (File.Exists(journalPath))
        {
            if (new FileInfo(journalPath).Length > 4 * 1024 * 1024) throw new IOException("Update recovery journal is too large.");
            var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllBytes(journalPath)) ?? throw new IOException("Missing recovery journal.");
            if (journal.SchemaVersion != 1 || journal.State is not ("prepared" or "committed")) throw new IOException("Unsupported recovery journal.");
            if (journal.State == "prepared")
            {
                // Verify every backup before changing any target. Retain the
                // journal if recovery cannot finish; retry remains idempotent.
                foreach (var entry in journal.Files.Where(e => e.Existed))
                    if (Hash(Child(transaction, "backup/" + entry.Path)) != entry.Sha256) throw new IOException("Update recovery backup does not match its journal.");
                foreach (var entry in journal.Files.Reverse())
                {
                    string destination = Child(root, entry.Path);
                    if (!entry.Existed) { File.Delete(destination); continue; }
                    string replacement = destination + ".ajn-restore";
                    File.Copy(Child(transaction, "backup/" + entry.Path), replacement, true);
                    if (destination.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) && File.Exists(destination))
                        File.Move(destination, Child(transaction, "recovering-updater.exe"), true);
                    File.Move(replacement, destination, true);
                }
                Write(journalPath, journal with { State = "committed" });
            }
        }
        File.Delete(Child(transaction, "pending"));
        Cleanup(transaction);
        if (Directory.Exists(transaction)) throw new IOException("Close the previous updater before retrying this update. Recovery completed; no addon data was removed.");
    }
    private static void Cleanup(string transaction)
    {
        // Verify each resolved descendant before deleting this updater-owned
        // directory. Refuse links instead of following them during cleanup.
        if (!Directory.Exists(transaction)) return;
        foreach (var file in FilesIn(transaction))
        {
            _ = Child(transaction, Path.GetRelativePath(transaction, file));
            try { File.Delete(file); } catch (IOException) { return; }
        }
        foreach (var directory in Directory.EnumerateDirectories(transaction, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length))
        { _ = Child(transaction, Path.GetRelativePath(transaction, directory)); Directory.Delete(directory); }
        Directory.Delete(transaction);
    }
}
