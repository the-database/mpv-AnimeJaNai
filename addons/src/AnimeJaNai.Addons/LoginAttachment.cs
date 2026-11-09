using AnimeJaNai.Addons.Management;

namespace AnimeJaNai.Addons;

internal static class LoginAttachment
{
    public static async Task<int> RunAsync(string installRoot, string dataRoot, CancellationToken cancellationToken = default)
    {
        using var activity = InstallationActivity.Acquire(installRoot);
        var settings = new LoginSettings(installRoot, dataRoot);
        if (!settings.IsEnabled) return 0;
        dataRoot = SafeFiles.DirectoryPath(dataRoot);
        string path = Path.Combine(dataRoot, "login.lock"); SafeFiles.CheckParents(path);
        FileStream held;
        try { held = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return 0; } // Already attached for this data directory.
        using (held)
        {
            for (int attempt = 0; attempt < 3 && settings.IsEnabled; attempt++)
            {
                try
                {
                    await using var client = await ManagementClient.ConnectOrStartAsync(dataRoot,
                        Path.Combine(installRoot, "addon-host", "ajn-addon.exe"), Path.Combine(installRoot, "addon-host", "runtime", "wasmtime.exe"),
                        installRoot, kind: "on_login", cancellationToken: cancellationToken);
                    while (settings.IsEnabled && !InstallationActivity.Pending(installRoot))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                        if ((await client.CallAsync("lifecycle.ping", cancellationToken: cancellationToken))?["connected"]?.GetValue<bool>() != true) return 0;
                    }
                    return 0;
                }
                catch (ManagementException error) when (error.Code is "login_disabled" or "handshake_required" or "update_in_progress") { return 0; }
                catch (Exception error) when (error is IOException or TimeoutException)
                {
                    if (InstallationActivity.Pending(installRoot)) return 0;
                    if (attempt == 2) throw;
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }
            }
        }
        return 0;
    }
}
