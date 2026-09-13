using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons;

internal static class SafeFiles
{
    public static string DirectoryPath(string root, params string[] segments)
    {
        string path = Path.GetFullPath(root);
        CheckParents(path);
        Directory.CreateDirectory(path);
        foreach (string segment in segments)
        {
            Contract.Require(Contract.ValidKey(segment) || Contract.ValidId(segment), "invalid_path", "Invalid storage segment.");
            path = Path.Combine(path, segment);
            CheckParents(path);
            Directory.CreateDirectory(path);
        }
        return path;
    }

    public static void CheckParents(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (File.Exists(current) || Directory.Exists(current))
                Contract.Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0,
                    "invalid_path", "Addon storage cannot use links or junctions.");
    }

    public static FileStream Lock(string directory)
    {
        string path = Path.Combine(directory, ".lock");
        CheckParents(path);
        var timer = Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (timer.ElapsedMilliseconds < 1000) { Thread.Sleep(10); }
        }
    }

    public static void AtomicWrite(string path, byte[] contents)
    {
        CheckParents(path);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed class AddonStorage
{
    public const int MaxStateBytes = 1024 * 1024;
    public const int MaxValueBytes = 32 * 1024;
    private readonly string directory;
    private string StatePath => Path.Combine(directory, "state.json");

    public AddonStorage(string root, string addonId)
    {
        Contract.Require(Contract.ValidId(addonId), "invalid_id", "Invalid addon id.");
        directory = SafeFiles.DirectoryPath(root, "data", addonId);
    }

    public JsonNode? Get(string key)
    {
        ValidateKey(key);
        using var held = SafeFiles.Lock(directory);
        return Read()[key]?.DeepClone();
    }

    public void Set(string key, JsonNode? value)
    {
        ValidateKey(key);
        Contract.Require(JsonSerializer.SerializeToUtf8Bytes(value, Contract.Json).Length <= MaxValueBytes,
            "storage_quota", "Value exceeds 32 KiB.");
        using var held = SafeFiles.Lock(directory);
        var state = Read();
        Contract.Require(state.ContainsKey(key) || state.Count < 256, "storage_quota", "Storage key limit reached.");
        state[key] = value?.DeepClone();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, Contract.Json);
        Contract.Require(bytes.Length <= MaxStateBytes, "storage_quota", "Addon storage exceeds 1 MiB.");
        SafeFiles.AtomicWrite(StatePath, bytes);
    }

    private JsonObject Read()
    {
        SafeFiles.CheckParents(StatePath);
        if (!File.Exists(StatePath)) return [];
        try { return Contract.ParseObject(AddonPackage.ReadBoundedFile(StatePath, MaxStateBytes)); }
        catch (AddonException) { throw new AddonException("invalid_storage", "Stored data is invalid; it has been preserved for recovery."); }
    }

    private static void ValidateKey(string key) => Contract.Require(Contract.ValidKey(key), "invalid_key", "Invalid storage key.");
}

public sealed record Activation(string Hash, string[] Permissions);
public sealed record InstalledAddon(Activation Current, Activation? Previous);

public sealed class AddonRegistry(string root)
{
    private string DirectoryFor(string id)
    {
        Contract.Require(Contract.ValidId(id), "invalid_id", "Invalid addon id.");
        return SafeFiles.DirectoryPath(root, "installed", id);
    }

    public void Install(AddonPackage package, IEnumerable<string> permissions)
    {
        var grant = new PermissionGrant(package, permissions);
        string directory = DirectoryFor(package.Manifest.Id);
        using var held = SafeFiles.Lock(directory);
        var existing = ReadState(directory);
        string packagePath = Path.Combine(directory, package.Hash + ".ajnaddon");
        SafeFiles.CheckParents(packagePath);
        if (!File.Exists(packagePath))
        {
            string temporary = packagePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                package.Save(temporary);
                Contract.Require(AddonPackage.Load(temporary).Hash == package.Hash, "integrity_mismatch", "Staged package changed.");
                File.Move(temporary, packagePath);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        Contract.Require(AddonPackage.Load(packagePath).Hash == package.Hash, "integrity_mismatch", "Installed package changed.");
        var state = new InstalledAddon(new(package.Hash, grant.Allowed.Order(StringComparer.Ordinal).ToArray()),
            existing?.Current.Hash != package.Hash ? existing?.Current : existing?.Previous);
        SafeFiles.AtomicWrite(Path.Combine(directory, "active.json"), JsonSerializer.SerializeToUtf8Bytes(state, Contract.Json));
    }

    public (AddonPackage Package, PermissionGrant Grant) Load(string id)
    {
        string directory = DirectoryFor(id);
        using var held = SafeFiles.Lock(directory);
        var state = ReadState(directory) ?? throw new AddonException("not_installed", "Addon is not installed.");
        return Resolve(directory, id, state.Current);
    }

    public void Rollback(string id)
    {
        string directory = DirectoryFor(id);
        using var held = SafeFiles.Lock(directory);
        var state = ReadState(directory) ?? throw new AddonException("not_installed", "Addon is not installed.");
        var previous = state.Previous ?? throw new AddonException("no_previous_version", "No previous package is available.");
        _ = Resolve(directory, id, previous);
        SafeFiles.AtomicWrite(Path.Combine(directory, "active.json"),
            JsonSerializer.SerializeToUtf8Bytes(new InstalledAddon(previous, state.Current), Contract.Json));
    }

    public void Disable(string id)
    {
        string directory = DirectoryFor(id);
        using var held = SafeFiles.Lock(directory);
        string path = Path.Combine(directory, "active.json");
        SafeFiles.CheckParents(path);
        File.Delete(path);
    }

    private static InstalledAddon? ReadState(string directory)
    {
        string path = Path.Combine(directory, "active.json");
        SafeFiles.CheckParents(path);
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = AddonPackage.ReadBoundedFile(path, 16 * 1024);
            _ = Contract.ParseObject(bytes);
            var state = JsonSerializer.Deserialize<InstalledAddon>(bytes, Contract.Json)
                ?? throw new AddonException("invalid_registration", "Invalid addon registration.");
            Contract.Require(state.Current is not null, "invalid_registration", "Missing addon activation.");
            return state;
        }
        catch (JsonException) { throw new AddonException("invalid_registration", "Invalid addon registration."); }
    }

    private static (AddonPackage, PermissionGrant) Resolve(string directory, string id, Activation activation)
    {
        Contract.Require(activation is not null && activation.Hash is not null && Contract.ValidHash(activation.Hash) &&
            activation.Permissions is not null, "invalid_registration", "Invalid addon activation.");
        string path = Path.Combine(directory, activation!.Hash + ".ajnaddon");
        SafeFiles.CheckParents(path);
        var package = AddonPackage.Load(path);
        Contract.Require(package.Hash == activation.Hash && package.Manifest.Id == id, "integrity_mismatch", "Registered package changed.");
        return (package, new PermissionGrant(package, activation.Permissions));
    }
}
