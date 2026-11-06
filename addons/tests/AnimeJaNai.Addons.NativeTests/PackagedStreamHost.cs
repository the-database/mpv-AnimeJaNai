using System.Diagnostics;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons.Management;

// Own the actual distributed host process, including its self-contained runtime
// and native-worker launch path. Only this test-created process may be stopped.
internal sealed class PackagedStreamHost : IAsyncDisposable
{
    private readonly Process process;
    private readonly string data, output;
    private readonly Task<string> stdout, stderr;
    private ManagementClient? client;
    private PackagedStreamHost(Process process, string data, string output)
    { this.process = process; this.data = data; this.output = output; stdout = process.StandardOutput.ReadToEndAsync(); stderr = process.StandardError.ReadToEndAsync(); }
    internal static async Task<PackagedStreamHost> StartAsync(string root, string data, string output)
    {
        string directory = Path.Combine(root, "addon-host");
        var info = new ProcessStartInfo(Path.Combine(directory, "ajn-addon.exe")) {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "serve", data, Path.Combine(directory, "runtime", "wasmtime.exe"), root }) info.ArgumentList.Add(arg);
        var host = new PackagedStreamHost(Process.Start(info)!, data, output);
        try { await host.ConnectAsync(); return host; }
        catch { await host.DisposeAsync(); throw; }
    }
    internal async Task ConnectAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while (true)
        {
            if (process.HasExited) throw new IOException("Packaged stream host exited before accepting management.");
            try { client = await ManagementClient.ConnectAsync(data, deadline.Token); return; }
            catch (Exception error) when (error is IOException or TimeoutException) { }
            await Task.Delay(100, deadline.Token);
        }
    }
    internal Task<JsonNode?> CallAsync(string method, JsonObject parameters) =>
        (client ?? throw new InvalidOperationException("Test Manager is disconnected.")).CallAsync(method, parameters);
    internal async Task DisconnectAsync() { if (client is not null) await client.DisposeAsync(); client = null; }
    internal async Task<bool> AnyRunningAsync()
    {
        var listed = await CallAsync("addons.list", new());
        return listed!["addons"]!.AsArray().Any(a => a?["running"]?.GetValue<bool>() == true);
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!process.HasExited)
            {
                try
                {
                    if (client is null) await ConnectAsync();
                    var listed = await CallAsync("addons.list", new());
                    foreach (var addon in listed!["addons"]!.AsArray())
                        await CallAsync("addons.stop", new() { ["id"] = addon!["id"]!.DeepClone() });
                }
                catch (Exception) { }
                await DisconnectAsync();
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40)); }
                catch { process.Kill(true); await process.WaitForExitAsync(); throw new IOException("Packaged stream host failed to drain and exit normally."); }
            }
            if (process.ExitCode != 0) throw new IOException("Packaged stream host exited with " + process.ExitCode);
        }
        finally
        {
            await DisconnectAsync();
            File.WriteAllText(Path.Combine(output, "packaged-host.log"), await stdout + "\n" + await stderr);
            process.Dispose();
        }
    }
}
