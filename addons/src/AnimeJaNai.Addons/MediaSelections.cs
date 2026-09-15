using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons;

public sealed record ApprovedSource(string Id, string Name, string Path);
public sealed record ApprovedProfile(string Id, string Name, int Slot, string Backend, string Configuration);
public sealed record ResolvedMedia(ApprovedSource Source, ApprovedProfile Profile);

// Trusted management writes approvals; the guest receives only ids and labels.
// Resource consent belongs to one exact package, independently of addon data.
public sealed class MediaSelections(string root)
{
    private sealed record State(int Version, string Hash, List<ApprovedSource> Sources, List<ApprovedProfile> Profiles);
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mkv", ".webm", ".mp4", ".m4v", ".mov", ".avi", ".ts", ".mts", ".m2ts", ".srt", ".ass", ".ssa", ".vtt" };
    private string DirectoryFor(AddonPackage package, PermissionGrant grant)
    {
        Contract.Require(package.Hash == grant.PackageHash, "invalid_grant", "Resource consent belongs to a different package.");
        grant.Demand("sessions.manage");
        return SafeFiles.DirectoryPath(root, "media-approvals", package.Manifest.Id);
    }

    public JsonObject List(AddonPackage package, PermissionGrant grant, bool includePaths = false)
    {
        string directory = DirectoryFor(package, grant);
        using var held = SafeFiles.Lock(directory);
        var state = Read(directory, package.Hash);
        return new()
        {
            ["sources"] = new JsonArray(state.Sources.Select(s => (JsonNode?)new JsonObject
                { ["id"] = s.Id, ["name"] = s.Name, ["path"] = includePaths ? s.Path : null }).ToArray()),
            ["profiles"] = new JsonArray(state.Profiles.Select(p => (JsonNode?)new JsonObject
                { ["id"] = p.Id, ["name"] = p.Name, ["slot"] = p.Slot, ["backend"] = p.Backend }).ToArray()),
        };
    }

    public string ApproveSource(AddonPackage package, PermissionGrant grant, string path)
    {
        string directory = DirectoryFor(package, grant);
        Contract.Require(Path.IsPathFullyQualified(path) && path.Length <= 1024 && !path.StartsWith(@"\\", StringComparison.Ordinal),
            "invalid_source", "Select a local media file on a drive. Network paths are not supported by this preview.");
        path = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
        {
            string drive = Path.GetPathRoot(path)!;
            Contract.Require(drive.Length == 3 && drive[1] == ':' && new DriveInfo(drive).DriveType != DriveType.Network,
                "invalid_source", "Select a local media file. Network sources require a separate destination grant.");
        }
        Contract.Require(Extensions.Contains(Path.GetExtension(path)) && path.IndexOf(':', 2) < 0,
            "invalid_source", "Select a supported media container.");
        SafeFiles.CheckParents(path);
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Contract.Require(file.Length > 0, "invalid_source", "Selected media is empty.");
        using var held = SafeFiles.Lock(directory);
        var state = Read(directory, package.Hash);
        var existing = state.Sources.Find(s => s.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing.Id;
        Contract.Require(state.Sources.Count < 16, "capacity_exceeded", "At most 16 selected sources per addon are supported.");
        string id = Guid.NewGuid().ToString("N");
        string name = new(Path.GetFileName(path).Where(c => !char.IsControl(c)).Take(128).ToArray());
        state.Sources.Add(new(id, name, path)); Write(directory, state); return id;
    }

    public string ApproveProfile(AddonPackage package, PermissionGrant grant, string name, int slot, string backend, string configuration)
    {
        string directory = DirectoryFor(package, grant);
        Contract.Require(name.Length is > 0 and <= 128 && !name.Any(char.IsControl), "invalid_profile", "Use a profile name of 1–128 characters.");
        Contract.Require(slot is >= 1 and <= 9 or >= 1001 and <= 1003 or >= 1010 and <= 1013, "invalid_profile", "Unsupported profile slot.");
        Contract.Require(backend is "DirectML" or "TensorRT", "invalid_profile", "Unsupported profile backend.");
        Contract.Require(!configuration.Contains('\0') && Encoding.UTF8.GetByteCount(configuration) <= 48 * 1024,
            "invalid_profile", "Configuration snapshot exceeds 48 KiB or contains invalid text.");
        // The native parser merges repeated global sections. The explicit
        // selection wins over an old backend value in the saved configuration.
        configuration = configuration.TrimEnd() + "\n[global]\nbackend=" + backend + "\nlogging=no\n";
        using var held = SafeFiles.Lock(directory);
        var state = Read(directory, package.Hash);
        Contract.Require(state.Profiles.Count < 8, "capacity_exceeded", "At most eight profile snapshots per addon are supported.");
        string id = Guid.NewGuid().ToString("N");
        state.Profiles.Add(new(id, name, slot, backend, configuration)); Write(directory, state); return id;
    }

    public ResolvedMedia Resolve(AddonPackage package, PermissionGrant grant, string sourceId, string? profileId)
    {
        string directory = DirectoryFor(package, grant);
        using var held = SafeFiles.Lock(directory);
        var state = Read(directory, package.Hash);
        var source = state.Sources.Find(s => s.Id == sourceId);
        Contract.Require(source is not null, "source_not_granted", "This media source has not been approved for this addon version.");
        return new(source, SelectProfile(state, profileId));
    }
    public ApprovedSource ResolveSource(AddonPackage package, PermissionGrant grant, string sourceId)
    {
        string directory = DirectoryFor(package, grant);
        using var held = SafeFiles.Lock(directory);
        var source = Read(directory, package.Hash).Sources.Find(s => s.Id == sourceId);
        Contract.Require(source is not null, "source_not_granted", "This media source is not approved for this addon version.");
        return source;
    }

    public ApprovedProfile ResolveProfile(AddonPackage package, PermissionGrant grant, string? profileId)
    {
        string directory = DirectoryFor(package, grant);
        using var held = SafeFiles.Lock(directory);
        return SelectProfile(Read(directory, package.Hash), profileId);
    }

    private static ApprovedProfile SelectProfile(State state, string? profileId)
    {
        if (profileId is null && state.Profiles.Count == 1) profileId = state.Profiles[0].Id;
        var profile = state.Profiles.Find(p => p.Id == profileId);
        Contract.Require(profile is not null, "profile_not_granted", "Choose an approved profile snapshot for this addon version.");
        return profile;
    }

    // Caller stops the addon first, draining pending opens and active sessions.
    public void Revoke(AddonPackage package, PermissionGrant grant, string kind, string id)
    {
        string directory = DirectoryFor(package, grant);
        Contract.Require(kind is "source" or "profile", "invalid_request", "Unknown media approval kind.");
        using var held = SafeFiles.Lock(directory);
        var state = Read(directory, package.Hash);
        if (kind == "source") state.Sources.RemoveAll(s => s.Id == id);
        else state.Profiles.RemoveAll(p => p.Id == id);
        Write(directory, state);
    }

    public void Clear(string addonId)
    {
        Contract.Require(Contract.ValidId(addonId), "invalid_id", "Invalid addon id.");
        string directory = SafeFiles.DirectoryPath(root, "media-approvals", addonId);
        using var held = SafeFiles.Lock(directory);
        string path = Path.Combine(directory, "approvals.json");
        SafeFiles.CheckParents(path); File.Delete(path);
    }

    private static State Read(string directory, string hash)
    {
        string path = Path.Combine(directory, "approvals.json");
        SafeFiles.CheckParents(path);
        if (!File.Exists(path)) return new(1, hash, [], []);
        try
        {
            var state = Contract.ParseObject(AddonPackage.ReadBoundedFile(path, 1024 * 1024)).Deserialize<State>(Contract.Json);
            Contract.Require(state is not null && state.Version == 1 && state.Hash is not null && Contract.ValidHash(state.Hash) &&
                state.Sources is { Count: <= 16 } && state.Profiles is { Count: <= 8 }, "invalid_approvals", "Invalid media approvals.");
            Contract.Require(state.Sources.All(s => s is not null && Guid.TryParseExact(s.Id, "N", out _) &&
                s.Name is { Length: <= 128 } && s.Path is { Length: <= 1024 } && Path.IsPathFullyQualified(s.Path)) &&
                state.Profiles.All(p => p is not null && Guid.TryParseExact(p.Id, "N", out _) && p.Name is { Length: <= 128 } &&
                    p.Configuration is { Length: <= 50000 } && p.Backend is "DirectML" or "TensorRT" &&
                    p.Slot is >= 1 and <= 9 or >= 1001 and <= 1003 or >= 1010 and <= 1013),
                "invalid_approvals", "Invalid media approvals.");
            return state.Hash == hash ? state : new(1, hash, [], []);
        }
        catch (Exception error) when (error is JsonException or AddonException or ArgumentException)
        { throw new AddonException("invalid_approvals", "Media approvals are invalid and have been preserved for recovery."); }
    }
    private static void Write(string directory, State state)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state, Contract.Json);
        Contract.Require(bytes.Length <= 1024 * 1024, "capacity_exceeded", "Media approvals exceed 1 MiB.");
        SafeFiles.AtomicWrite(Path.Combine(directory, "approvals.json"), bytes);
    }
}
