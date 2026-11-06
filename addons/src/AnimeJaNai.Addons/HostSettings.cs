using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons;

// Trusted host policy. Guests cannot read/write this file or call management
// methods. Native admission uses the same gate as a limit update, so a newly
// accepted open observes either the old or new policy, never a partial change.
public sealed class HostSettings
{
    internal object Sync { get; } = new();
    private readonly string path;
    private readonly int? commandLineLimit;
    private JsonObject saved;
    private int limit;
    public HostSettings(string root, int? commandLineLimit = null)
    {
        Contract.Require(commandLineLimit is null or (>= 1 and <= 16), "invalid_host_settings", "Host capacity must be between 1 and 16.");
        path = Path.Combine(SafeFiles.DirectoryPath(root), "host-settings.json");
        SafeFiles.CheckParents(path);
        saved = File.Exists(path) ? Contract.ParseObject(AddonPackage.ReadBoundedFile(path, 4096)) :
            new() { ["schemaVersion"] = 1L, ["maximumConcurrentSessions"] = 2L };
        Contract.Require(Contract.Number(saved, "schemaVersion") == 1, "unsupported_host_settings", "This host settings version is unsupported. The saved file has been preserved.");
        long stored = Contract.Number(saved, "maximumConcurrentSessions");
        Contract.Require(stored is >= 1 and <= 16, "invalid_host_settings", "Saved host capacity must be between 1 and 16. The file has been preserved.");
        this.commandLineLimit = commandLineLimit; limit = commandLineLimit ?? (int)stored;
    }
    public int MaximumConcurrentSessions { get { lock (Sync) return limit; } }
    public JsonObject Describe()
    {
        lock (Sync) return new() {
            ["maximumConcurrentSessions"] = limit, ["minimum"] = 1, ["maximum"] = 16,
            ["editable"] = commandLineLimit is null, ["source"] = commandLineLimit is null ? "saved" : "commandLine",
        };
    }
    public JsonObject Update(long maximumConcurrentSessions)
    {
        Contract.Require(maximumConcurrentSessions is >= 1 and <= 16, "invalid_host_settings", "Choose a whole number between 1 and 16.");
        lock (Sync)
        {
            Contract.Require(commandLineLimit is null, "host_settings_locked", "The host was started with an explicit capacity. Restart it without that argument to use saved settings.");
            var next = (JsonObject)saved.DeepClone(); next["maximumConcurrentSessions"] = maximumConcurrentSessions;
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(next);
            Contract.Require(bytes.Length <= 4096, "invalid_host_settings", "Host settings exceed their file limit.");
            SafeFiles.AtomicWrite(path, bytes);
            saved = next; limit = (int)maximumConcurrentSessions;
            return Describe();
        }
    }
}
