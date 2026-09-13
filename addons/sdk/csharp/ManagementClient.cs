// Trusted AJN management client, protocol 1. Not part of the guest addon API.
// Kept standalone so Manager does not need a cross-repository project reference.
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeJaNai.Addons.Management;

public sealed class ManagementException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class ManagementClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream pipe;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly byte[] buffer = new byte[4096];
    private int position, available;
    private long nextId;
    private JsonObject serverInfo = new();
    public JsonObject ServerInfo => (JsonObject)serverInfo.DeepClone();

    private ManagementClient(NamedPipeClientStream pipe) { this.pipe = pipe; }
    public bool IsConnected => !lifetime.IsCancellationRequested && pipe.IsConnected;
    public static string PipeName(string root) => "AJN.Addons.v1." + Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())))[..32];

    public static Task<ManagementClient> ConnectAsync(string root, CancellationToken cancellationToken = default) =>
        ConnectCoreAsync(root, null, cancellationToken);

    // Trusted player/login bridges use a fixed, limited connection role. This
    // does not expose player state, arbitrary commands, or any guest transport.
    public static Task<ManagementClient> ConnectLifecycleAsync(string root, string kind, CancellationToken cancellationToken = default)
    {
        if (kind is not ("on_player" or "on_login")) throw new ArgumentException("Unknown lifecycle kind.", nameof(kind));
        return ConnectCoreAsync(root, kind, cancellationToken);
    }

    private static async Task<ManagementClient> ConnectCoreAsync(string root, string? kind, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", PipeName(root), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var client = new ManagementClient(pipe);
        try
        {
            await pipe.ConnectAsync(1500, cancellationToken).ConfigureAwait(false);
            var hello = await client.CallAsync(kind is null ? "manager.hello" : "lifecycle.hello",
                new JsonObject { ["major"] = 1, ["kind"] = kind }, cancellationToken).ConfigureAwait(false);
            if (hello?["major"]?.GetValue<int>() != 1) throw new ManagementException("incompatible_api", "Unsupported addon host version.");
            if (kind is not null && hello?["kind"]?.GetValue<string>() != kind)
                throw new ManagementException("incompatible_api", "The addon host does not support this lifecycle connection.");
            client.serverInfo = (JsonObject)hello.DeepClone();
            return client;
        }
        catch { await client.DisposeAsync(); throw; }
    }

    public static async Task<ManagementClient> ConnectOrStartAsync(string root, string hostPath, string runtimePath,
        string? nativeRoot = null, string? kind = null, CancellationToken cancellationToken = default)
    {
        Task<ManagementClient> Connect(CancellationToken token) => kind is null ? ConnectAsync(root, token) : ConnectLifecycleAsync(root, kind, token);
        try { return await Connect(cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is TimeoutException or IOException) { }
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(hostPath) || !File.Exists(runtimePath)) throw new IOException("The addon host or runtime is missing from this AJN build.");
        var info = new ProcessStartInfo(Path.GetFullPath(hostPath)) {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(hostPath))!,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string argument in new[] { "serve", Path.GetFullPath(root), Path.GetFullPath(runtimePath) }) info.ArgumentList.Add(argument);
        if (nativeRoot is not null) info.ArgumentList.Add(Path.GetFullPath(nativeRoot));
        using var started = Process.Start(info) ?? throw new IOException("Could not start the addon host.");
        _ = DrainAsync(started.StandardOutput); _ = DrainAsync(started.StandardError);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            while (true)
            {
                try { return await Connect(deadline.Token).ConfigureAwait(false); }
                catch (Exception retry) when (retry is TimeoutException or IOException) { }
                // Another player/Manager may have won the exclusive host lease.
                // Our short-lived contender exiting is not a connection failure.
                await Task.Delay(150, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new IOException("The addon host did not become available. Check its runtime and saved host settings."); }
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        try { char[] bytes = new char[1024]; while (await reader.ReadAsync(bytes).ConfigureAwait(false) != 0) { } }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }

    public async Task<JsonNode?> CallAsync(string method, JsonObject? parameters = null, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(35));
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            long id = ++nextId;
            var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters?.DeepClone() ?? new JsonObject() };
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message);
            if (bytes.Length > 128 * 1024) throw new ManagementException("message_too_large", "Management request exceeds 128 KiB.");
            await pipe.WriteAsync(bytes, linked.Token).ConfigureAwait(false);
            await pipe.WriteAsync(new byte[] { 10 }, linked.Token).ConfigureAwait(false);
            await pipe.FlushAsync(linked.Token).ConfigureAwait(false);
            var response = await ReadAsync(linked.Token).ConfigureAwait(false);
            if (response["jsonrpc"]?.GetValue<string>() != "2.0" || response["id"]?.GetValue<long>() != id ||
                response.ContainsKey("result") == response.ContainsKey("error")) throw new IOException("Invalid management response.");
            if (response["error"] is JsonObject error)
                throw new ManagementException(error["data"]?["code"]?.GetValue<string>() ?? "host_error", error["message"]?.GetValue<string>() ?? "Addon host operation failed.");
            return response["result"]?.DeepClone();
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            await DisposeAsync(); throw;
        }
        finally { gate.Release(); }
    }

    public async Task<JsonArray> ListAsync(CancellationToken cancellationToken = default)
    {
        var all = new JsonArray();
        string? cursor = null;
        for (int page = 0; page < 16; page++)
        {
            var result = await CallAsync("addons.list", new JsonObject { ["after"] = cursor }, cancellationToken).ConfigureAwait(false) as JsonObject
                ?? throw new IOException("Invalid addon listing.");
            if (result["addons"] is not JsonArray items) throw new IOException("Invalid addon listing.");
            foreach (var item in items) all.Add(item?.DeepClone());
            string? next = result["nextCursor"]?.GetValue<string>();
            if (next is null) return all;
            if (next == cursor) throw new IOException("Invalid addon listing cursor.");
            cursor = next;
        }
        throw new IOException("Addon listing exceeds the supported limit.");
    }

    private async Task<JsonObject> ReadAsync(CancellationToken cancellationToken)
    {
        using var line = new MemoryStream();
        while (true)
        {
            if (position == available)
            {
                available = await pipe.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); position = 0;
                if (available == 0) throw new IOException("Addon host disconnected.");
            }
            int newline = Array.IndexOf(buffer, (byte)10, position, available - position);
            int end = newline < 0 ? available : newline;
            if (line.Length + end - position > 128 * 1024) throw new IOException("Management response exceeds 128 KiB.");
            line.Write(buffer, position, end - position);
            position = newline < 0 ? available : newline + 1;
            if (newline >= 0) return JsonNode.Parse(line.ToArray(), documentOptions: new JsonDocumentOptions { MaxDepth = 24 }) as JsonObject
                ?? throw new IOException("Invalid management response.");
        }
    }

    public ValueTask DisposeAsync()
    {
        lifetime.Cancel(); pipe.Dispose(); return ValueTask.CompletedTask;
    }
}
