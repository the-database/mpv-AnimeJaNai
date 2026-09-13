using System.Text.Json.Nodes;
using System.Security.Cryptography;

namespace AnimeJaNai.Addons.Native;

internal sealed class NativeSessionProvider : IProcessingSessionProvider
{
    private readonly string root, work;
    private readonly MediaSelections selections;
    private readonly WorkerCommand command;
    private readonly int maximumSessions;
    private readonly object gate;
    private readonly HostSettings? hostSettings;
    private int Capacity => hostSettings?.MaximumConcurrentSessions ?? maximumSessions;
    private int reserved;
    private readonly bool framesAvailable;
    private readonly bool outputsAvailable;
    private readonly NetworkSelections networkSelections;

    public NativeSessionProvider(string installRoot, string dataRoot, MediaSelections selections, WorkerCommand command, int maximumSessions = 2, NetworkSelections? networkSelections = null, HostSettings? hostSettings = null)
    {
        Contract.Require(OperatingSystem.IsWindows() && IntPtr.Size == 8, "feature_unavailable", "Native sessions require Windows x64.");
        Contract.Require(maximumSessions is >= 1 and <= 16, "invalid_request", "Native session capacity must be between 1 and 16.");
        root = Path.GetFullPath(installRoot);
        foreach (string relative in new[] { "libmpv-2.dll", "animejanai/inference/aji.dll" })
            Contract.Require(File.Exists(Path.Combine(root, relative)), "native_unavailable", "Native media runtime is incomplete.");
        this.selections = selections; this.command = command; this.maximumSessions = maximumSessions;
        this.hostSettings = hostSettings; gate = hostSettings?.Sync ?? new();
        this.networkSelections = networkSelections ?? new(dataRoot);
        work = SafeFiles.DirectoryPath(dataRoot, "media-workers");
        // Added only by a package containing the matching private native filter.
        // Older native previews remain compatible and do not advertise samples.
        framesAvailable = HasFrameRuntime(root);
        outputsAvailable = HasOutputRuntime(root);
    }

    internal static bool HasOutputRuntime(string root)
    {
        try
        {
            var marker = Contract.ParseObject(AddonPackage.ReadBoundedFile(Path.Combine(root, "addon-host", "native-output.json"), 4096));
            if (Contract.Number(marker, "privateOutputAbi") != 1 || Contract.Text(marker, "cRuntime", 16) != "ucrt") return false;
            foreach (var (name, key) in new[] { ("libmpv-2.dll", "mpvSha256"), ("avformat-63.dll", "avformatSha256") })
            {
                string expected = Contract.Text(marker, key, 64);
                if (!Contract.ValidHash(expected)) return false;
                using var library = File.OpenRead(Path.Combine(root, name));
                if (Convert.ToHexStringLower(SHA256.HashData(library)) != expected) return false;
            }
            return true;
        }
        catch (Exception error) when (error is IOException or AddonException or UnauthorizedAccessException) { return false; }
    }

    internal static bool HasFrameRuntime(string root)
    {
        try
        {
            var marker = Contract.ParseObject(AddonPackage.ReadBoundedFile(Path.Combine(root, "addon-host", "native-frames.json"), 4096));
            if (Contract.Number(marker, "privateSampleAbi") != 1) return false;
            string expected = Contract.Text(marker, "mpvSha256", 64);
            if (!Contract.ValidHash(expected)) return false;
            using var library = File.OpenRead(Path.Combine(root, "libmpv-2.dll"));
            return Convert.ToHexStringLower(SHA256.HashData(library)) == expected;
        }
        catch (Exception error) when (error is IOException or AddonException or UnauthorizedAccessException) { return false; }
    }

    public Task<IProcessingSession> OpenAsync(string sourceId, string? profileId, CancellationToken token) =>
        throw new AddonException("permission_denied", "Native media requires a scoped addon owner.");
    public IProcessingSessionProvider ForAddon(AddonPackage package, PermissionGrant grant) => new Bound(this, package, grant);

    private sealed class Bound(NativeSessionProvider host, AddonPackage package, PermissionGrant grant) : IProcessingSessionProvider, IProcessingSelectionProvider
    {
        public int ApiMinor => 1;
        public bool SupportsFrames => host.framesAvailable;
        public bool SupportsOutputs => host.outputsAvailable;
        public bool SupportsRemoteSources => true;
        public JsonObject RemoteSourceFormats() => new()
        {
            ["types"] = new JsonArray("http"), ["protocols"] = new JsonArray("http", "https"),
            ["containers"] = new JsonArray("matroska", "webm", "mov", "avi", "mpegts"),
            ["playlists"] = false, ["redirects"] = false,
            ["seek"] = "Validated byte ranges with a strong entity tag or an eligible Last-Modified date",
            ["maximumBytes"] = RemoteMediaStream.MaximumBytes, ["maximumBytesPerSecond"] = RemoteMediaStream.BytesPerSecond,
            ["maximumReadBytes"] = RemoteMediaStream.MaximumRead, ["maximumRequests"] = RemoteMediaStream.MaximumRequests,
            ["ioDeadlineSeconds"] = 10, ["maximumWallSeconds"] = 86400, ["maximumConcurrentSessions"] = host.Capacity,
        };
        public JsonObject OutputFormats() => new()
        {
            ["encoders"] = new JsonArray("nvenc"), ["requires"] = "Supported NVIDIA GPU and driver; Windows UCRT native output runtime",
            ["videoCodecs"] = new JsonArray("h264", "hevc", "av1"), ["containers"] = new JsonArray("matroska", "mpegts", "fragmentedMp4"),
            ["audioCodecs"] = new JsonArray("none", "aac", "opus"), ["destinations"] = new JsonArray("httpUpload"),
            ["mpegtsVideoCodecs"] = new JsonArray("h264", "hevc"), ["mpegtsAudioCodecs"] = new JsonArray("none", "aac"),
            ["minimumVideoKbps"] = 256, ["maximumVideoKbps"] = 50000, ["minimumAudioKbps"] = 32, ["maximumAudioKbps"] = 512,
            ["minimumKeyframeFrames"] = 1, ["maximumKeyframeFrames"] = 600,
            ["maximumWallSeconds"] = 86400, ["maximumBytes"] = OutputUploadSession.MaximumBytes,
            ["maximumBytesPerSecond"] = OutputUploadSession.BytesPerSecond, ["maximumHostBytesPerSecond"] = OutputUploadSession.GlobalBytesPerSecond,
            ["maximumConcurrentSessions"] = host.Capacity, ["softwareSubtitles"] = false,
        };
        public JsonObject ListSelections()
        {
            var result = host.selections.List(package, grant);
            result["maximumConcurrentSessions"] = host.Capacity;
            return result;
        }
        public Task<IProcessingSession> OpenAsync(string sourceId, string? profileId, CancellationToken token) => OpenCoreAsync(sourceId, profileId, null, token);
        public Task<IProcessingSession> OpenRemoteAsync(RemoteInputRequest source, string? profileId, CancellationToken token) =>
            OpenCoreAsync(null, profileId, null, token, source);
        public Task<IProcessingSession> OpenRemoteOutputAsync(RemoteInputRequest source, string? profileId, OutputRequest output, CancellationToken token)
        {
            grant.Demand("media.output"); grant.Demand("network.connect");
            Contract.Require(SupportsOutputs, "feature_unavailable", "This native runtime does not offer encoded output.");
            output.Validate(); return OpenCoreAsync(null, profileId, output, token, source);
        }
        public Task<IProcessingSession> OpenOutputAsync(string sourceId, string? profileId, OutputRequest output, CancellationToken token)
        {
            grant.Demand("media.output"); grant.Demand("network.connect");
            Contract.Require(SupportsOutputs, "feature_unavailable", "This native runtime does not offer encoded output.");
            output.Validate();
            return OpenCoreAsync(sourceId, profileId, output, token);
        }
        private async Task<IProcessingSession> OpenCoreAsync(string? sourceId, string? profileId, OutputRequest? output, CancellationToken token, RemoteInputRequest? remoteSource = null)
        {
            token.ThrowIfCancellationRequested();
            var selected = remoteSource is null ? host.selections.Resolve(package, grant, sourceId!, profileId) : null;
            var profile = selected?.Profile ?? host.selections.ResolveProfile(package, grant, profileId);
            if (profile.Backend == "TensorRT")
                Contract.Require(new[] { "nvinfer_11.dll", "trtexec.exe", "aji_trt.dll" }.All(name => File.Exists(Path.Combine(host.root, "animejanai", "inference", name))),
                    "backend_unavailable", "The selected TensorRT runtime is not installed. Install it through AJN Manager or approve a DirectML profile.");
            lock (host.gate)
            {
                Contract.Require(host.reserved < host.Capacity, "capacity_exceeded", "Native processing capacity is in use. Close a session before starting another.");
                host.reserved++;
            }
            try
            {
                // Resolve/decrypt only after reserving capacity, but before any
                // native work or outbound connection can begin.
                var plan = output is null ? null : OutputUploadPlan.Prepare(package, grant, host.networkSelections, output);
                var input = remoteSource is null ? null : RemoteInputPlan.Prepare(package, grant, host.networkSelections, remoteSource);
                var process = await MediaProcess.StartAsync(host.root, selected?.Source.Path, profile.Configuration,
                    profile.Slot, profile.Backend, host.work, host.command, token,
                    enableFrameSamples: host.framesAvailable && grant.Allowed.Contains("frames.read"), encoding: output?.NativeOptions, remoteSource: input);
                try { return new Reserved(host, plan is null ? process : new OutputUploadSession(process, plan)); }
                catch
                {
                    try { await process.DisposeAsync(); }
                    catch (Exception cleanup) { throw new ProcessingSessionStartException(process, cleanup); }
                    throw;
                }
            }
            catch (ProcessingSessionStartException error)
            {
                throw new ProcessingSessionStartException(new Reserved(host, error.Session), error);
            }
            catch { lock (host.gate) host.reserved--; throw; }
        }
    }
    private sealed class Reserved(NativeSessionProvider host, IProcessingSession process) : IControllableProcessingSession, IFrameProcessingSession
    {
        private int released;
        public Task<JsonObject> GetStatusAsync(CancellationToken token) => process.GetStatusAsync(token);
        public Task PauseAsync(bool paused, CancellationToken token) => ((IControllableProcessingSession)process).PauseAsync(paused, token);
        public Task SeekAsync(double seconds, CancellationToken token) => ((IControllableProcessingSession)process).SeekAsync(seconds, token);
        public IFrameSubscription SubscribeFrames(FrameRequest request) => ((IFrameProcessingSession)process).SubscribeFrames(request);
        public async ValueTask DisposeAsync()
        {
            await process.DisposeAsync();
            if (Interlocked.Exchange(ref released, 1) == 0) lock (host.gate) host.reserved--;
        }
    }
}
