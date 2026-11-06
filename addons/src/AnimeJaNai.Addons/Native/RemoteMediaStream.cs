using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons.Native;

// A bounded HTTP reader owned by one trusted native worker. libmpv receives
// bytes through the private stream callback and never sees an HTTP URL.
internal sealed class RemoteMediaStream : Stream
{
    internal const long MaximumBytes = 512L << 30;
    internal const int MaximumRead = 64 * 1024, BytesPerSecond = 8 << 20, MaximumRequests = 65536;
    internal static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(10);
    private readonly RemoteInputPlan plan;
    private readonly HttpClient client;
    private readonly CancellationTokenSource lifetime;
    private readonly SemaphoreSlim io = new(1, 1);
    private HttpResponseMessage? response;
    private Stream? body;
    private long position, length = -1, end = -1, received, nextRead, nextRequest;
    private int requests, disposed, cancelRequested;
    private bool seekable, initialized;
    private string? entityTag;
    private DateTimeOffset? modified;
    private string? failure;
    private readonly string representationNonce = Guid.NewGuid().ToString("N");
    internal string RepresentationId => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
        plan.IdentityKey + "\n" + length + "\n" + (entityTag ?? modified?.ToString("O") ?? representationNonce))));
    private Task? cancellation;

    private RemoteMediaStream(RemoteInputPlan plan, TimeSpan lifetimeLimit)
    {
        Contract.Require(lifetimeLimit > TimeSpan.Zero && lifetimeLimit <= TimeSpan.FromHours(24), "invalid_input", "Invalid media lifetime bound.");
        plan.Validate(); this.plan = plan;
        lifetime = new(lifetimeLimit);
        var handler = NetworkAccess.CreateHandler(plan.Destination);
        handler.MaxResponseDrainSize = 0; // Discarding an old range must not download its remaining body.
        client = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal static async Task<RemoteMediaStream> OpenAsync(RemoteInputPlan plan, CancellationToken token = default, TimeSpan? lifetimeLimit = null)
    {
        var stream = new RemoteMediaStream(plan, lifetimeLimit ?? TimeSpan.FromHours(24));
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, stream.lifetime.Token);
            deadline.CancelAfter(IoTimeout);
            await stream.OpenRangeAsync(deadline.Token).ConfigureAwait(false);
            return stream;
        }
        catch (Exception error)
        {
            var translated = stream.Translate(error, token);
            stream.Dispose(); throw translated;
        }
    }

    public override bool CanRead => Volatile.Read(ref disposed) == 0 && failure is null && !lifetime.IsCancellationRequested;
    public override bool CanSeek => CanRead && seekable;
    public override bool CanWrite => false;
    public override long Length => Interlocked.Read(ref length) is >= 0 and var size ? size : throw new NotSupportedException("Remote length is unknown.");
    public override long Position { get => Interlocked.Read(ref position); set => Seek(value, SeekOrigin.Begin); }
    internal JsonObject Status()
    {
        if (Volatile.Read(ref disposed) == 0 && lifetime.IsCancellationRequested && Volatile.Read(ref cancelRequested) == 0)
            Interlocked.CompareExchange(ref failure, "input_limit", null);
        return new() {
            ["type"] = "http", ["seekable"] = CanSeek, ["bytesRead"] = Interlocked.Read(ref received),
            ["requests"] = Volatile.Read(ref requests), ["errorCode"] = failure,
        };
    }

    private async Task OpenRangeAsync(CancellationToken token)
    {
        Contract.Require(++requests <= MaximumRequests, "input_limit", "Remote media exceeded its request limit.");
        await WaitUntilAsync(nextRequest, token).ConfigureAwait(false);
        nextRequest = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 32;
        using var request = new HttpRequestMessage(HttpMethod.Get, plan.Target)
        { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        request.Headers.Range = new RangeHeaderValue(position, null);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        if (initialized)
        {
            Contract.Require(seekable, "input_not_seekable", "This media response does not support consistent range reads.");
            request.Headers.IfRange = entityTag is not null ? new RangeConditionHeaderValue(new EntityTagHeaderValue(entityTag))
                : new RangeConditionHeaderValue(modified!.Value);
        }
        if (plan.Header is not null) request.Headers.TryAddWithoutValidation(plan.Header, plan.Credential);
        foreach (var header in plan.DelegatedHeaders) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        Contract.Require(response.Content.Headers.ContentEncoding.All(v => v.Equals("identity", StringComparison.OrdinalIgnoreCase)),
            "input_encoding", "Remote media must use identity content encoding.");
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            Contract.Require(range is { Unit: "bytes", HasRange: true, HasLength: true } && range.From == position &&
                range.To >= position && range.Length > range.To && range.Length <= MaximumBytes,
                "input_range", "The media service returned an invalid or oversized byte range.");
            Contract.Require(response.Content.Headers.ContentLength is null || response.Content.Headers.ContentLength == range.To - range.From + 1,
                "input_range", "The media response length disagrees with its byte range.");
            if (!initialized)
            {
                length = range.Length!.Value;
                var tag = response.Headers.ETag;
                if (tag is { IsWeak: false } && tag.Tag != "*") entityTag = tag.Tag;
                // Only fall back to a date when no entity tag is present. The
                // conservative interval also avoids recent one-second changes.
                else if (tag is null && response.Content.Headers.LastModified is { } date &&
                    response.Headers.Date is { } now && now - date >= TimeSpan.FromSeconds(60)) modified = date;
                seekable = entityTag is not null || modified is not null;
            }
            else
            {
                Contract.Require(range.Length == length && (entityTag is not null
                    ? response.Headers.ETag is { IsWeak: false } tag && tag.Tag == entityTag
                    : response.Content.Headers.LastModified == modified),
                    "input_changed", "The remote media changed during playback.");
            }
            end = range.To!.Value;
            Contract.Require(seekable || end == length - 1, "input_not_seekable", "Segmented media needs a consistent range validator.");
        }
        else
        {
            Contract.Require(!initialized && response.StatusCode == HttpStatusCode.OK,
                initialized ? "input_changed" : "input_status",
                initialized ? "The media service stopped honoring its range validator." : "The approved media service did not return a media response.");
            Contract.Require(response.Content.Headers.ContentRange is null, "input_range", "Unexpected range metadata on a full media response.");
            length = response.Content.Headers.ContentLength ?? -1;
            Contract.Require(length <= MaximumBytes, "input_limit", "Remote media exceeds the input size limit.");
            end = length < 0 ? -1 : length - 1;
        }
        body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        initialized = true;
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        deadline.CancelAfter(IoTimeout);
        bool acquired = false;
        try
        {
            await io.WaitAsync(deadline.Token).ConfigureAwait(false); acquired = true;
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            deadline.Token.ThrowIfCancellationRequested();
            if (buffer.Length == 0) return 0;
            while (true)
            {
                if (body is null)
                {
                    if (length >= 0 && position == length) return 0;
                    await OpenRangeAsync(deadline.Token).ConfigureAwait(false);
                }
                if (end >= 0 && position == end + 1)
                {
                    // A chunked response must also finish at its declared range boundary.
                    if (response!.Content.Headers.ContentLength is null)
                    {
                        byte[] probe = new byte[1];
                        Contract.Require(await body!.ReadAsync(probe, deadline.Token).ConfigureAwait(false) == 0,
                            "input_range", "The media response exceeded its declared byte range.");
                    }
                    CloseResponse();
                    if (position == length) return 0;
                    continue;
                }
                Contract.Require(received < MaximumBytes, "input_limit", "Remote media exceeded its transfer limit.");
                await WaitUntilAsync(nextRead, deadline.Token).ConfigureAwait(false);
                int count = (int)Math.Min(Math.Min(buffer.Length, MaximumRead), MaximumBytes - received);
                if (end >= 0) count = (int)Math.Min(count, end - position + 1);
                int read = await body!.ReadAsync(buffer[..count], deadline.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    Contract.Require(end < 0 || position == end + 1, "input_truncated", "The media response ended before its declared length.");
                    if (length < 0) length = position;
                    CloseResponse(); return 0;
                }
                Interlocked.Add(ref position, read); Interlocked.Add(ref received, read);
                nextRead = Stopwatch.GetTimestamp() + (long)((double)read * Stopwatch.Frequency / BytesPerSecond);
                return read;
            }
        }
        catch (Exception error) { throw Translate(error, cancellationToken); }
        finally { if (acquired) io.Release(); }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        io.Wait(lifetime.Token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (!CanSeek) throw new NotSupportedException("This remote media is forward-only.");
            long target = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => checked(position + offset),
                SeekOrigin.End => checked(length + offset), _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
            if (target < 0 || target > length) throw new IOException("Seek is outside the remote media.");
            if (target != position) { CloseResponse(); Interlocked.Exchange(ref position, target); }
            return target;
        }
        finally { io.Release(); }
    }

    private static async Task WaitUntilAsync(long due, CancellationToken token)
    {
        double seconds = (double)(due - Stopwatch.GetTimestamp()) / Stopwatch.Frequency;
        if (seconds > 0) await Task.Delay(TimeSpan.FromSeconds(seconds), token).ConfigureAwait(false);
    }
    private Exception Translate(Exception error, CancellationToken caller)
    {
        var result = error switch
        {
            AddonException known => known,
            OperationCanceledException => new AddonException(caller.IsCancellationRequested || Volatile.Read(ref cancelRequested) != 0
                ? "input_cancelled" : lifetime.IsCancellationRequested ? "input_limit" : "input_timeout",
                "Remote media reading was cancelled or exceeded its time limit."),
            HttpIOException { HttpRequestError: HttpRequestError.ResponseEnded } => new AddonException("input_truncated", "The media response ended before its declared length."),
            _ => new AddonException("input_unavailable", "The approved media service could not complete this read."),
        };
        Interlocked.CompareExchange(ref failure, result.Code, null); Cancel(); return result;
    }
    private void CloseResponse() { body?.Dispose(); body = null; response?.Dispose(); response = null; }
    internal void Cancel()
    {
        Interlocked.Exchange(ref cancelRequested, 1);
        try { cancellation ??= lifetime.CancelAsync(); }
        catch (ObjectDisposedException) { }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
        {
            Cancel(); io.Wait();
            try { CloseResponse(); client.Dispose(); cancellation?.GetAwaiter().GetResult(); lifetime.Dispose(); }
            finally { io.Release(); }
        }
        base.Dispose(disposing);
    }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
