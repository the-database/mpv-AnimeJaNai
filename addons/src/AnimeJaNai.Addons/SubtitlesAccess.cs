using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons.Native;

namespace AnimeJaNai.Addons;

internal sealed class SubtitlesAccess(PermissionGrant grant, ISubtitleProvider provider, HttpServerAccess servers, RequestCredentials credentials) : IAsyncDisposable
{
    private static readonly SemaphoreSlim Capacity = new(4, 4);
    private readonly object sync = new();
    private readonly Dictionary<string, Job> jobs = [];
    private bool disposed;
    private sealed class Job(RequestCredentials.Lease? credential)
    {
        internal readonly RequestCredentials.Lease? Credential = credential;
        internal readonly CancellationTokenSource Stop = CancellationTokenSource.CreateLinkedTokenSource(credential?.Token ?? default);
        internal Task Work = Task.CompletedTask;
        internal byte[]? Bytes;
        internal string? Error, Etag;
        internal readonly HashSet<Task> Readers = [];
    }
    internal JsonObject Formats()
    {
        Demand(); return new() { ["burn"] = true, ["extract"] = new JsonArray("webvtt"),
            ["textCodecs"] = new JsonArray("ass", "ssa", "subrip", "webvtt", "mov_text", "text"),
            ["bitmapExtraction"] = false, ["preservesAssLayout"] = false, ["timestampTimeline"] = "source",
            ["maximumJobs"] = 2, ["maximumHostJobs"] = 4, ["maximumResultBytes"] = NativeSubtitles.MaximumBytes,
            ["maximumChunkBytes"] = 32768, ["maximumSeconds"] = 300 };
    }
    internal string Open(ProbeRequest source, SubtitleRequest options)
    {
        lock (sync)
        {
            Demand(); grant.Demand("media.input");
            if (source.Remote is not null) grant.Demand("network.connect");
            Contract.Require((source.SourceId is null) != (source.Remote is null), "invalid_source", "Choose one approved subtitle source.");
            options.Validate(); Contract.Require(jobs.Count < 2 && Capacity.Wait(0), "capacity_exceeded", "Close a subtitle result before starting another extraction.");
            Job job;
            try { job = new(source.Remote?.CredentialId is string id ? credentials.Acquire(id, source.Remote.DestinationId) : null); }
            catch { Capacity.Release(); throw; }
            string key = Guid.NewGuid().ToString("N"); jobs.Add(key, job);
            job.Work = Task.Run(async () =>
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(job.Stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(300));
                try
                {
                    byte[] bytes = await provider.ExtractSubtitlesAsync(source, options, deadline.Token); deadline.Token.ThrowIfCancellationRequested();
                    Contract.Require(bytes.Length is >= 8 and <= NativeSubtitles.MaximumBytes && bytes.AsSpan(0, 6).SequenceEqual("WEBVTT"u8), "invalid_subtitle_result", "Native subtitle output was incomplete or invalid.");
                    string etag = '"' + Convert.ToHexStringLower(SHA256.HashData(bytes)) + '"';
                    lock (sync) { job.Bytes = bytes; job.Etag = etag; }
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                { lock (sync) job.Error = error is AddonException addon ? addon.Code : error is OperationCanceledException ? "subtitles_cancelled" : "subtitles_failed"; }
                // Capacity covers retained result memory, not just native work. Close releases it.
            });
            return key;
        }
    }
    internal JsonObject Status(string id)
    {
        lock (sync)
        {
            var job = Find(id); bool revoked = job.Stop.IsCancellationRequested;
            return new() { ["subtitleId"] = id, ["state"] = !job.Work.IsCompleted ? "pending" : revoked || job.Error is not null ? "failed" : "completed",
                ["byteLength"] = revoked ? null : job.Bytes?.Length, ["contentType"] = "text/vtt; charset=utf-8", ["timestampTimeline"] = "source",
                ["error"] = revoked || job.Error is not null ? new JsonObject { ["code"] = revoked ? "subtitles_cancelled" : job.Error, ["message"] = "Subtitle extraction stopped or could not complete." } : null };
        }
    }
    internal BrokerResponse Read(string id, int offset, int count)
    {
        lock (sync)
        {
            var job = Ready(id); byte[] bytes = job.Bytes!;
            Contract.Require(offset >= 0 && offset <= bytes.Length && count is >= 1 and <= 32768, "invalid_request", "Invalid subtitle chunk offset or size.");
            int length = Math.Min(count, bytes.Length - offset);
            return new(new JsonObject { ["byteLength"] = length, ["offset"] = offset, ["totalBytes"] = bytes.Length, ["eof"] = offset + length == bytes.Length }, bytes.AsMemory(offset, length));
        }
    }
    internal void Serve(string requestId, string id)
    {
        lock (sync)
        {
            var job = Ready(id); byte[] bytes = job.Bytes!; string etag = job.Etag!;
            job.Readers.RemoveWhere(t => t.IsCompleted);
            Contract.Require(job.Readers.Count < 16, "capacity_exceeded", "This subtitle result already has 16 readers.");
            Task delivery = servers.ClaimNative(requestId, async (context, token) =>
            {
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, job.Stop.Token);
                stop.CancelAfter(TimeSpan.FromSeconds(120));
                stop.Token.ThrowIfCancellationRequested();
                var request = context.Request; var response = context.Response;
                if (request.Method is not ("GET" or "HEAD")) { response.StatusCode = 405; response.Headers.Allow = "GET, HEAD"; return; }
                response.ContentType = "text/vtt; charset=utf-8"; response.Headers.ETag = etag;
                response.Headers.AcceptRanges = "bytes"; response.Headers.CacheControl = "private, no-cache"; response.Headers["X-Content-Type-Options"] = "nosniff";
                if (request.Headers.TryGetValue("If-Match", out var match) && !StreamHttpDelivery.TagsMatch(match.ToString(), etag, false)) { response.StatusCode = 412; return; }
                if (request.Headers.TryGetValue("If-None-Match", out var none) && StreamHttpDelivery.TagsMatch(none.ToString(), etag, true)) { response.StatusCode = 304; return; }
                long start = 0, end = bytes.Length - 1;
                if (request.Headers.TryGetValue("Range", out var range) && (!request.Headers.TryGetValue("If-Range", out var validator) || validator.ToString() == etag))
                {
                    if (!StreamHttpDelivery.TryRange(range.ToString(), bytes.Length, out start, out end)) { response.StatusCode = 416; response.Headers.ContentRange = "bytes */" + bytes.Length; return; }
                    response.StatusCode = 206; response.Headers.ContentRange = FormattableString.Invariant($"bytes {start}-{end}/{bytes.Length}");
                }
                response.ContentLength = end - start + 1;
                if (request.Method == "HEAD") return;
                for (long offset = start; offset <= end;)
                {
                    int length = (int)Math.Min(32768, end - offset + 1);
                    using var io = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); io.CancelAfter(TimeSpan.FromSeconds(15));
                    await response.Body.WriteAsync(bytes.AsMemory((int)offset, length), io.Token); offset += length;
                }
            });
            job.Readers.Add(delivery);
            _ = delivery.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
    }
    internal void Cancel(string id) { lock (sync) { var job = Find(id); job.Stop.Cancel(); job.Bytes = null; } }
    internal void Close(string id)
    {
        lock (sync)
        {
            var job = Find(id); Contract.Require(job.Work.IsCompleted, "subtitles_active", "Cancel and wait for subtitle extraction before closing.");
            job.Stop.Cancel(); job.Bytes = null;
            Contract.Require(job.Readers.All(t => t.IsCompleted), "subtitles_active", "Subtitle responses are closing. Retry close after they finish.");
            job.Credential?.Dispose(); job.Stop.Dispose(); jobs.Remove(id); Capacity.Release();
        }
    }
    private Job Ready(string id)
    {
        var job = Find(id); Contract.Require(job.Work.IsCompleted && job.Bytes is not null && !job.Stop.IsCancellationRequested, "subtitles_not_ready", "Wait for a successful subtitle extraction."); return job;
    }
    private Job Find(string id)
    { Demand(); Contract.Require(jobs.TryGetValue(id, out var job), "subtitles_not_found", "Subtitle result belongs to another addon or was closed."); return job; }
    private void Demand()
    { grant.Demand("sessions.manage"); Contract.Require(!disposed && provider.SupportsSubtitles, "feature_unavailable", "The subtitle service is unavailable."); }
    public async ValueTask DisposeAsync()
    {
        Job[] all; lock (sync) { disposed = true; all = jobs.Values.ToArray(); foreach (var job in all) job.Stop.Cancel(); }
        await Task.WhenAll(all.Select(j => j.Work));
        try { await Task.WhenAll(all.SelectMany(j => j.Readers)); } catch { /* Canceled native responses are already owned and closed. */ }
        lock (sync) { foreach (var job in all) { job.Bytes = null; job.Credential?.Dispose(); job.Stop.Dispose(); Capacity.Release(); } jobs.Clear(); }
    }
}
