using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons.Native;

// The host provisions one reviewed model, never an addon-supplied URL or path.
// A grant to player.analysis permits this optional download and GPU operation.
internal sealed class NativeAnalysisModel(string installRoot) : IDisposable
{
    internal const string Sha256 = "f15622d853e8260172812b657053460e20806f04b9e05147d49af7bed31a6e99";
    internal const string FileName = "isnetis." + Sha256 + ".onnx";
    private readonly object sync = new();
    private CancellationTokenSource? cancellation;
    private int owners, generation;
    private string state = "idle";
    private string? failure;
    internal bool Ready { get { lock (sync) return state == "ready"; } }
    internal JsonObject Status() { lock (sync) return new() { ["state"] = state, ["model"] = FrameRequest.Model, ["message"] = failure }; }

    internal IDisposable Acquire()
    {
        lock (sync)
        {
            if (++owners == 1 && state is "idle" or "failed")
            {
                cancellation?.Dispose();
                cancellation = new();
                state = "provisioningModel"; failure = null;
                int run = ++generation;
                var token = cancellation.Token;
                _ = Task.Run(() => ProvisionAsync(run, token));
            }
            return new Lease(this);
        }
    }

    private async Task ProvisionAsync(int run, CancellationToken token)
    {
        string directory = Path.Combine(installRoot, "animejanai", "analysis");
        string destination = Path.Combine(directory, FileName), temporary = destination + "." + run + ".partial";
        try
        {
            SafeFiles.CheckParents(directory); Directory.CreateDirectory(directory);
            if (File.Exists(destination))
            {
                await using var existing = File.OpenRead(destination);
                if (Convert.ToHexStringLower(await SHA256.HashDataAsync(existing, token)) != Sha256)
                    throw new IOException("The installed character-mask model has an incorrect checksum. Remove that model from animejanai/analysis and re-enable the addon.");
            }
            else
            {
                await DownloadAsync(temporary, token);
                await using (var file = File.OpenRead(temporary))
                    if (Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token)) != Sha256)
                        throw new IOException("Character-mask model download failed checksum verification.");
                token.ThrowIfCancellationRequested();
                File.Move(temporary, destination);
            }
            lock (sync) if (run == generation) state = "ready";
        }
        catch (OperationCanceledException) { lock (sync) if (run == generation) state = "idle"; }
        catch (Exception error) { lock (sync) if (run == generation) { state = "failed"; failure = "Character-mask model setup failed: " + error.Message; } }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task DownloadAsync(string temporary, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        token = deadline.Token;
        var uri = new Uri("https://huggingface.co/skytnt/anime-seg/resolve/main/isnetis.onnx");
        // HF redirects to its model CDN. Resolve and pin each connection, with
        // no proxy, cookies or credentials; the content hash remains mandatory.
        for (int redirects = 0; redirects < 6; ++redirects)
        {
            if (uri.Scheme != "https" || uri.UserInfo.Length != 0)
                throw new IOException("Character-mask download redirected outside HTTPS.");
            var destination = await NetworkDestination.InspectAsync(uri.GetLeftPart(UriPartial.Authority), token);
            using var client = new HttpClient(NetworkAccess.CreateHandler(destination)) { Timeout = Timeout.InfiniteTimeSpan };
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
            { uri = new Uri(uri, location); continue; }
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(token);
            await using var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            byte[] buffer = new byte[65536]; long length = 0; int count;
            while ((count = await source.ReadAsync(buffer, token)) != 0)
            {
                length += count;
                if (length > 200 * 1024 * 1024) throw new IOException("Character-mask model exceeds its approved download size.");
                await file.WriteAsync(buffer.AsMemory(0, count), token);
            }
            return;
        }
        throw new IOException("Character-mask download exceeded its redirect limit.");
    }

    private void Release()
    {
        // Provision the approved, checksum-pinned model once per host. Rapid
        // subscription changes reuse the in-flight download, not a new 200 MB
        // request. Host shutdown still cancels it through Dispose.
        lock (sync) --owners;
    }
    public void Dispose() { lock (sync) { ++generation; cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null; } }
    private sealed class Lease(NativeAnalysisModel owner) : IDisposable
    {
        private int closed;
        public void Dispose() { if (Interlocked.Exchange(ref closed, 1) == 0) owner.Release(); }
    }
}
