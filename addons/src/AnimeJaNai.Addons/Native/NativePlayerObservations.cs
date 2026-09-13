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
    internal PlayerFrameRegistry Frames { get; } = new();

    internal static bool Available(string root)
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8) return false;
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
            try
            {
                _ = process.SafeHandle;
                string? executable = process.MainModule?.FileName;
                Contract.Require(new[] { "mpv.exe", "mpvnet.exe" }.Any(name =>
                    string.Equals(executable, Path.Combine(root, name), StringComparison.OrdinalIgnoreCase)),
                    "invalid_player", "Observation requires a player from this AJN installation.");
                Contract.Require(!attached.Values.Any(a => a.Source.Process.Id == processId && !a.Source.Process.HasExited),
                    "player_attached", "This player already has an observation bridge.");
                source = new(process);
                registration = Frames.Register(source, out _);
                attached.Add(client, new(source, registration));
                return Poll(client);
            }
            catch { registration?.Dispose(); source?.Dispose(); if (source is null) process.Dispose(); throw; }
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
            return new JsonObject { ["available"] = true, ["mapping"] = attachment.Source.Buffer.Name,
                ["enabled"] = attachment.Source.Buffer.HasSubscription };
        }
    }
    internal void Disconnect(string client)
    {
        lock (sync) { if (attached.Remove(client, out var attachment)) attachment.Registration.Dispose(); }
    }
    public void Dispose()
    {
        lock (sync)
        {
            Frames.Dispose(); attached.Clear();
        }
    }
    private sealed record Attachment(NativeSource Source, IDisposable Registration);
    private sealed class NativeSource(Process process) : IPlayerFrameSource
    {
        internal Process Process { get; } = process;
        internal NativeFrameBuffer Buffer { get; } = new(player: true);
        public IFrameSubscription Subscribe(FrameRequest request) => Buffer.Subscribe(request, repeatLatest: true);
        public void Dispose() { Buffer.Dispose(); Process.Dispose(); }
    }
}
