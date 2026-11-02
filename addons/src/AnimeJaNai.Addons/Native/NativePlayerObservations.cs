using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons.Native;

// Trusted lifecycle adapter. Guest code sees registry IDs and validated reduced
// samples only. It cannot supply a process, mapping, filter or command string.
internal sealed class NativePlayerObservations(string installRoot) : IDisposable
{
    private readonly string root = Path.GetFullPath(installRoot);
    private readonly Dictionary<string, Attachment> attached = new(StringComparer.Ordinal);
    private readonly object sync = new();
    private readonly NativeAnalysisModel model = new(installRoot);
    internal PlayerFrameRegistry Frames { get; } = new(supportsAnalysis: NativeCapabilities.Has(installRoot, "privatePlayerAnalysisAbi"));
    internal SceneDetectionRegistry? Scenes { get; } = NativeCapabilities.Has(installRoot, "privateSceneAbi") ? new() : null;
    private readonly bool nativeMvtools = NativeCapabilities.Has(installRoot, "privateMvtoolsSceneAbi");
    private readonly bool nativeEnhanced = NativeCapabilities.Has(installRoot, "privateEnhancedSceneAbi");

    internal static bool Available(string root)
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8) return false;
        if (NativeCapabilities.Present(root)) return NativeCapabilities.Has(root, "privatePlayerSampleAbi");
        try
        {
            var marker = Contract.ParseObject(AddonPackage.ReadBoundedFile(Path.Combine(root, "addon-host", "native-player-frames.json"), 4096));
            if (Contract.Number(marker, "privatePlayerSampleAbi") != 1) return false;
            foreach (var (name, key) in new[] { ("libmpv-2.dll", "librarySha256"), ("mpv.exe", "playerSha256") })
            {
                string expected = Contract.Text(marker, key, 64);
                if (!Contract.ValidHash(expected)) return false;
                using var file = File.OpenRead(Path.Combine(root, name));
                if (Convert.ToHexStringLower(SHA256.HashData(file)) != expected) return false;
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or AddonException) { return false; }
    }

    internal JsonObject Attach(string client, int processId)
    {
        lock (sync)
        {
            Contract.Require(!attached.ContainsKey(client), "player_attached", "This lifecycle connection already has a player.");
            Contract.Require(attached.Count < PlayerFrameRegistry.MaximumPlayers, "capacity_exceeded", "Too many attached players.");
            Contract.Require(processId > 0, "invalid_player", "Expected an AJN player process.");
            var process = Process.GetProcessById(processId);
            NativeSource? source = null;
            IDisposable? registration = null;
            IDisposable? sceneRegistration = null;
            try
            {
                _ = process.SafeHandle;
                string? executable = process.MainModule?.FileName;
                Contract.Require(new[] { "mpv.exe", "mpvnet.exe" }.Any(name =>
                    string.Equals(executable, Path.Combine(root, name), StringComparison.OrdinalIgnoreCase)),
                    "invalid_player", "Observation requires a player from this AJN installation.");
                Contract.Require(!attached.Values.Any(a => a.Source.Process.Id == processId && !a.Source.Process.HasExited),
                    "player_attached", "This player already has an observation bridge.");
                source = new(process, Scenes is not null, nativeMvtools, nativeEnhanced, Frames.SupportsAnalysis, model);
                registration = Frames.Register(source, out string playerId);
                if (source.Scene is not null) sceneRegistration = Scenes!.Register(playerId, source.Scene);
                attached.Add(client, new(source, registration, sceneRegistration));
                return Poll(client);
            }
            catch { sceneRegistration?.Dispose(); registration?.Dispose(); source?.Dispose(); if (source is null) process.Dispose(); throw; }
        }
    }
    internal JsonObject Poll(string client)
    {
        lock (sync)
        {
            Contract.Require(attached.TryGetValue(client, out var attachment), "player_not_found", "Attach the player first.");
            if (attachment.Source.Process.HasExited)
            {
                Disconnect(client);
                throw new AddonException("player_closed", "The original player process has exited.");
            }
            attachment.Source.Buffer.RenewPlayerLease();
            attachment.Source.SourceBuffer?.RenewPlayerLease();
            attachment.Source.AnalysisBuffer?.RenewPlayerLease();
            attachment.Source.Scene?.RenewLease();
            return new JsonObject { ["available"] = true, ["mapping"] = attachment.Source.Buffer.Name,
                ["enabled"] = attachment.Source.Buffer.HasSubscription,
                ["sourceMapping"] = attachment.Source.SourceBuffer?.HasSubscription == true ? attachment.Source.SourceBuffer.Name : "",
                ["analysisMapping"] = attachment.Source.AnalysisBuffer?.HasSubscription == true && model.Ready ? attachment.Source.AnalysisBuffer.Name : "",
                ["sceneMapping"] = attachment.Source.Scene?.Enabled == true ? attachment.Source.Scene.Name : "" };
        }
    }
    internal void Disconnect(string client)
    {
        lock (sync) { if (attached.Remove(client, out var attachment)) { attachment.SceneRegistration?.Dispose(); attachment.Registration.Dispose(); } }
    }
    public void Dispose()
    {
        lock (sync)
        {
            Scenes?.Dispose(); Frames.Dispose(); model.Dispose(); attached.Clear();
        }
    }
    private sealed record Attachment(NativeSource Source, IDisposable Registration, IDisposable? SceneRegistration);
    private sealed class NativeSource(Process process, bool scenes, bool nativeMvtools, bool nativeEnhanced, bool analysis, NativeAnalysisModel model) : IPlayerFrameSource
    {
        internal Process Process { get; } = process;
        internal NativeFrameBuffer Buffer { get; } = new(player: true);
        // Allocate optional mappings only when the first corresponding reader subscribes.
        internal NativeFrameBuffer? SourceBuffer { get; private set; }
        internal NativeFrameBuffer? AnalysisBuffer { get; private set; }
        internal NativeSceneBuffer? Scene { get; } = scenes ? new(nativeMvtools, nativeEnhanced) : null;
        public IFrameSubscription Subscribe(FrameRequest request)
        {
            if (request.Stage == "processed") return Buffer.Subscribe(request, repeatLatest: true);
            Contract.Require(analysis, "feature_unavailable", "This native player has no source observation support.");
            if (!request.CharacterMask) return (SourceBuffer ??= new(player: true, stage: "source")).Subscribe(request, repeatLatest: true);
            var samples = (AnalysisBuffer ??= new(player: true, stage: "source", analysis: true)).Subscribe(request, repeatLatest: true);
            return new AnalysisLease(samples, model.Acquire(), model);
        }
        public void Dispose() { Scene?.Dispose(); Buffer.Dispose(); SourceBuffer?.Dispose(); AnalysisBuffer?.Dispose(); Process.Dispose(); }
    }
    private sealed class AnalysisLease(IFrameSubscription samples, IDisposable resource, NativeAnalysisModel model) : IFrameSubscription
    {
        public FramePacket? ReadLatest() => model.Ready ? samples.ReadLatest() : null;
        public JsonObject Status() => model.Ready ? samples.Status() : model.Status();
        public void Dispose() { samples.Dispose(); resource.Dispose(); }
    }
}
