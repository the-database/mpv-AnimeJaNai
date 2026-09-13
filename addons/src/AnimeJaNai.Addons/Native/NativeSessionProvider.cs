using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons.Native;

internal sealed class NativeSessionProvider : IProcessingSessionProvider
{
    private readonly string root, work;
    private readonly MediaSelections selections;
    private readonly WorkerCommand command;
    private readonly int maximumSessions;
    private readonly object gate = new();
    private int reserved;

    public NativeSessionProvider(string installRoot, string dataRoot, MediaSelections selections, WorkerCommand command, int maximumSessions = 2)
    {
        Contract.Require(OperatingSystem.IsWindows() && IntPtr.Size == 8, "feature_unavailable", "Native sessions require Windows x64.");
        Contract.Require(maximumSessions is >= 1 and <= 16, "invalid_request", "Native session capacity must be between 1 and 16.");
        root = Path.GetFullPath(installRoot);
        foreach (string relative in new[] { "libmpv-2.dll", "animejanai/inference/aji.dll" })
            Contract.Require(File.Exists(Path.Combine(root, relative)), "native_unavailable", "Native media runtime is incomplete.");
        this.selections = selections; this.command = command; this.maximumSessions = maximumSessions;
        work = SafeFiles.DirectoryPath(dataRoot, "media-workers");
    }

    public Task<IProcessingSession> OpenAsync(string sourceId, string? profileId, CancellationToken token) =>
        throw new AddonException("permission_denied", "Native media requires a scoped addon owner.");
    public IProcessingSessionProvider ForAddon(AddonPackage package, PermissionGrant grant) => new Bound(this, package, grant);

    private sealed class Bound(NativeSessionProvider host, AddonPackage package, PermissionGrant grant) : IProcessingSessionProvider, IProcessingSelectionProvider
    {
        public int ApiMinor => 1;
        public JsonObject ListSelections()
        {
            var result = host.selections.List(package, grant);
            result["maximumConcurrentSessions"] = host.maximumSessions;
            return result;
        }
        public async Task<IProcessingSession> OpenAsync(string sourceId, string? profileId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var selected = host.selections.Resolve(package, grant, sourceId, profileId);
            if (selected.Profile.Backend == "TensorRT")
                Contract.Require(new[] { "nvinfer_11.dll", "trtexec.exe", "aji_trt.dll" }.All(name => File.Exists(Path.Combine(host.root, "animejanai", "inference", name))),
                    "backend_unavailable", "The selected TensorRT runtime is not installed. Install it through AJN Manager or approve a DirectML profile.");
            lock (host.gate)
            {
                Contract.Require(host.reserved < host.maximumSessions, "capacity_exceeded", "Native processing capacity is in use. Close a session before starting another.");
                host.reserved++;
            }
            try
            {
                return new Reserved(host, await MediaProcess.StartAsync(host.root, selected.Source.Path, selected.Profile.Configuration,
                    selected.Profile.Slot, selected.Profile.Backend, host.work, host.command, token));
            }
            catch (ProcessingSessionStartException error)
            {
                throw new ProcessingSessionStartException(new Reserved(host, (MediaProcess)error.Session), error);
            }
            catch { lock (host.gate) host.reserved--; throw; }
        }
    }
    private sealed class Reserved(NativeSessionProvider host, MediaProcess process) : IControllableProcessingSession
    {
        private int released;
        public Task<JsonObject> GetStatusAsync(CancellationToken token) => process.GetStatusAsync(token);
        public Task PauseAsync(bool paused, CancellationToken token) => process.PauseAsync(paused, token);
        public Task SeekAsync(double seconds, CancellationToken token) => process.SeekAsync(seconds, token);
        public async ValueTask DisposeAsync()
        {
            await process.DisposeAsync();
            if (Interlocked.Exchange(ref released, 1) == 0) lock (host.gate) host.reserved--;
        }
    }
}
