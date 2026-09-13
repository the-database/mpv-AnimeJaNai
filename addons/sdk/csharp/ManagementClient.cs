// Trusted AJN management client, protocol 1. Not part of the guest addon API.
// Kept standalone so Manager does not need a cross-repository project reference.
using System;
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

    private ManagementClient(NamedPipeClientStream pipe) { this.pipe = pipe; }
    public bool IsConnected => !lifetime.IsCancellationRequested && pipe.IsConnected;
    public static string PipeName(string root) => "AJN.Addons.v1." + Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())))[..32];

    public static async Task<ManagementClient> ConnectAsync(string root, CancellationToken cancellationToken = default)
    {
        var pipe = new NamedPipeClientStream(".", PipeName(root), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var client = new ManagementClient(pipe);
        try
        {
            await pipe.ConnectAsync(1500, cancellationToken).ConfigureAwait(false);
            var hello = await client.CallAsync("manager.hello", new JsonObject { ["major"] = 1 }, cancellationToken).ConfigureAwait(false);
            if (hello?["major"]?.GetValue<int>() != 1) throw new ManagementException("incompatible_api", "Unsupported addon host version.");
            return client;
        }
        catch { await client.DisposeAsync(); throw; }
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
