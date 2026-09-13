using System.Diagnostics;
using AnimeJaNai.Addons.Management;
using System.Text;
using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons;

// One small trusted bridge per player. It has no media path or general command
// channel. Holding the original process handle avoids following a reused PID.
internal static class PlayerAttachment
{
    public static async Task<int> RunAsync(string installRoot, string dataRoot, int playerId, CancellationToken cancellationToken = default, string? instance = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Player addon activation currently supports Windows.");
        installRoot = Path.GetFullPath(installRoot); dataRoot = Path.GetFullPath(dataRoot);
        Contract.Require(playerId > 0 && playerId != Environment.ProcessId, "invalid_player", "Expected the owning AJN player process.");
        Contract.Require(instance is null || Contract.ValidKey(instance), "invalid_player", "Invalid player bridge instance.");
        string? control = instance is null ? null : Path.Combine(SafeFiles.DirectoryPath(dataRoot, "player-control"), instance + ".json");
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
                    bool observing = false;
                    long revision = 0;
                    if (control is not null)
                    {
                        var attached = await client.CallAsync("player.attach", new JsonObject { ["processId"] = playerId }, lifetime.Token);
                        observing = attached?["available"]?.GetValue<bool>() == true;
                    }
                    while (true)
                    {
                        if (observing)
                        {
                            var configuration = (JsonObject)(await client.CallAsync("player.poll", cancellationToken: lifetime.Token))!;
                            configuration["schemaVersion"] = 1; configuration["instance"] = instance;
                            configuration["revision"] = (++revision).ToString(System.Globalization.CultureInfo.InvariantCulture);
                            SafeFiles.AtomicWrite(control!, Encoding.UTF8.GetBytes(configuration.ToJsonString()));
                        }
                        else await client.CallAsync("lifecycle.ping", cancellationToken: lifetime.Token);
                        await Task.Delay(TimeSpan.FromMilliseconds(observing ? 500 : 5000), lifetime.Token);
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
        catch (ManagementException error) when (error.Code == "player_closed") { return 0; }
        finally
        {
            lifetime.Cancel(); await ownerExited;
            if (control is not null)
            {
                try { SafeFiles.CheckParents(control); File.Delete(control); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or AddonException) { }
            }
        }
    }

    private static async Task WatchOwnerAsync(Process owner, CancellationTokenSource lifetime)
    {
        try { await owner.WaitForExitAsync(lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { lifetime.Cancel(); }
    }
}
