using System.Diagnostics;
using AnimeJaNai.Addons.Management;

namespace AnimeJaNai.Addons;

// One small trusted bridge per player. It has no media path or player command
// channel. Holding the original process handle avoids following a reused PID.
internal static class PlayerAttachment
{
    public static async Task<int> RunAsync(string installRoot, string dataRoot, int playerId, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Player addon activation currently supports Windows.");
        installRoot = Path.GetFullPath(installRoot); dataRoot = Path.GetFullPath(dataRoot);
        Contract.Require(playerId > 0 && playerId != Environment.ProcessId, "invalid_player", "Expected the owning AJN player process.");
        using var player = Process.GetProcessById(playerId);
        _ = player.SafeHandle;
        string? executable = player.MainModule?.FileName;
        Contract.Require(new[] { "mpv.exe", "mpvnet.exe" }.Any(name =>
            string.Equals(executable, Path.Combine(installRoot, name), StringComparison.OrdinalIgnoreCase)),
            "invalid_player", "The lifecycle owner must be the player in this AJN installation.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ownerExited = WatchOwnerAsync(player, lifetime);
        try
        {
            // No retry loop can restart a faulty worker by itself. Reconnect only
            // when the entire host connection is lost, with three total attempts.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    await using var client = await ManagementClient.ConnectOrStartAsync(dataRoot,
                        Path.Combine(installRoot, "addon-host", "ajn-addon.exe"),
                        Path.Combine(installRoot, "addon-host", "runtime", "wasmtime.exe"),
                        File.Exists(Path.Combine(installRoot, "addon-host", "native-media.json")) ? installRoot : null,
                        kind: "on_player", cancellationToken: lifetime.Token);
                    while (true)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), lifetime.Token);
                        await client.CallAsync("lifecycle.ping", cancellationToken: lifetime.Token);
                    }
                }
                catch (Exception error) when (error is IOException or TimeoutException)
                {
                    if (attempt == 2) throw;
                    await Task.Delay(TimeSpan.FromSeconds(2), lifetime.Token);
                }
            }
            return 0;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return 0; }
        finally { lifetime.Cancel(); await ownerExited; }
    }

    private static async Task WatchOwnerAsync(Process owner, CancellationTokenSource lifetime)
    {
        try { await owner.WaitForExitAsync(lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { lifetime.Cancel(); }
    }
}
