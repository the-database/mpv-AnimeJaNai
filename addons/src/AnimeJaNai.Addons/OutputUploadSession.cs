using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons;

internal sealed record OutputUploadPlan(OutputRequest Request, NetworkDestination Destination, Uri Target, string? CredentialHeader, string? Credential)
{
    internal static OutputUploadPlan Prepare(AddonPackage package, PermissionGrant grant, NetworkSelections selections, OutputRequest request)
    {
        grant.Demand("media.output"); grant.Demand("sessions.manage"); grant.Demand("network.connect");
        request.Validate();
        var selected = selections.Resolve(package, grant, request.DestinationId);
        Contract.Require(selected.Destination.Scheme is "http" or "https", "invalid_output", "Encoded media requires an approved HTTP service.");
        var target = NetworkAccess.RequestTarget(selected.Destination, request.Path);
        string? credential = request.UseCredential ? selections.Credential(package, grant, selected) : null;
        if (credential is not null)
        {
            NetworkAccess.ValidateHeader(selected.CredentialHeader!, credential);
            using var check = new HttpRequestMessage();
            Contract.Require(check.Headers.TryAddWithoutValidation(selected.CredentialHeader!, credential),
                "invalid_credential", "Media upload credentials must use a request header, such as Authorization.");
        }
        return new(request, selected.Destination, target, credential is null ? null : selected.CredentialHeader, credential);
    }
}

// Owns both sides of one output. Completion/failure is retained until the public
// session is closed; registry/provider admission is not released by an early
// HTTP response or failed cleanup. Continuous bytes never enter a guest RPC.
internal sealed class OutputUploadSession : IControllableProcessingSession, IFrameProcessingSession
{
    internal const long MaximumBytes = 512L << 30;
    internal const int BytesPerSecond = 8 << 20, GlobalBytesPerSecond = 32 << 20;
    private static readonly Rate GlobalRate = new(GlobalBytesPerSecond);
    private readonly IEncodedProcessingSession producer;
    private readonly CancellationTokenSource lifetime = new(TimeSpan.FromHours(24));
    private readonly object sync = new();
    private readonly Task uploading;
    private readonly JsonObject output;
    private JsonObject lastNative = new();
    private string state = "opening";
    private string? errorCode, errorMessage;
    private int? httpStatus;
    private long bytes;
    private bool closing;
    private Task? cleanup;
    private Task<int>? pendingRead;

    internal OutputUploadSession(IEncodedProcessingSession producer, OutputUploadPlan plan)
    {
        this.producer = producer;
        output = new() { ["type"] = "httpUpload", ["destinationId"] = plan.Request.DestinationId,
            ["method"] = plan.Request.Method, ["encoding"] = plan.Request.NativeOptions.ToJson() };
        uploading = RunAsync(plan);
    }

    public async Task<JsonObject> GetStatusAsync(CancellationToken token)
    {
        bool active; lock (sync) active = state is "opening" or "running";
        if (active)
        {
            var current = await producer.GetStatusAsync(token);
            lock (sync) lastNative = (JsonObject)current.DeepClone();
        }
        lock (sync)
        {
            var result = (JsonObject)lastNative.DeepClone();
            result["state"] = state; result["output"] = output.DeepClone();
            result["output"]!["bytesSent"] = Interlocked.Read(ref bytes);
            result["output"]!["httpStatus"] = httpStatus;
            if (errorCode is not null) result["error"] = new JsonObject { ["code"] = errorCode, ["message"] = errorMessage };
            return result;
        }
    }
    public Task PauseAsync(bool paused, CancellationToken token)
    {
        lock (sync) Contract.Require(!closing && state is "opening" or "running", "session_closed", "Output session is no longer running.");
        return producer.PauseAsync(paused, token);
    }
    public Task SeekAsync(double seconds, CancellationToken token) =>
        throw new AddonException("operation_unavailable", "Open a new output session to change the stream timeline.");
    public IFrameSubscription SubscribeFrames(FrameRequest request)
    {
        lock (sync) Contract.Require(!closing && state is "opening" or "running", "session_closed", "Output session is no longer running.");
        return producer is IFrameProcessingSession frames ? frames.SubscribeFrames(request) :
            throw new AddonException("feature_unavailable", "This output producer does not offer frame samples.");
    }

    private async Task RunAsync(OutputUploadPlan plan)
    {
        try
        {
            using var handler = NetworkAccess.CreateHandler(plan.Destination);
            using var content = new MediaContent(this, producer.EncodedOutput);
            // Expect allows HTTP/1.1 response reads to run during upload. The
            // observer cancels live serialization on an early final response;
            // HttpClient otherwise waits for that serialization before returning.
            handler.Expect100ContinueTimeout = TimeSpan.FromMilliseconds(250);
            handler.PlaintextStreamFilter = (context, _) => ValueTask.FromResult<Stream>(new UploadResponseStream(context.PlaintextStream, status =>
            {
                if (content.Finished) return;
                lock (sync) httpStatus = status;
                Fail(status is >= 300 ? "output_rejected" : "output_incomplete", "The service ended the request before the encoded stream finished.");
                content.Stop();
            }));
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            content.Headers.ContentType = new MediaTypeHeaderValue(plan.Request.Container switch
                { "matroska" => "video/x-matroska", "mpegts" => "video/mp2t", _ => "video/mp4" });
            using var request = new HttpRequestMessage(new HttpMethod(plan.Request.Method), plan.Target)
                { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact, Content = content };
            request.Headers.ExpectContinue = true;
            if (plan.Credential is not null)
                Contract.Require(request.Headers.TryAddWithoutValidation(plan.CredentialHeader!, plan.Credential), "invalid_credential", "This credential header cannot be used for media upload.");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
            lock (sync) httpStatus = (int)response.StatusCode;
            Contract.Require(response.IsSuccessStatusCode, "output_rejected", "The approved service rejected the media upload. See its HTTP status.");
            Contract.Require(content.Finished, "output_incomplete", "The service responded before the complete encoded stream was sent.");
            // EOF follows the native writer closing. Allow the status message
            // immediately after that close to reach the supervisor as well.
            var deadline = Stopwatch.StartNew();
            while (true)
            {
                var native = await producer.GetStatusAsync(lifetime.Token);
                lock (sync) lastNative = (JsonObject)native.DeepClone();
                string? nativeState = native["state"]?.GetValue<string>();
                Contract.Require(nativeState is not ("failed" or "closed"), "native_output_failed", "Native processing did not produce a complete output stream.");
                if (nativeState == "completed") break;
                Contract.Require(deadline.Elapsed < TimeSpan.FromSeconds(5), "native_output_failed", "Native output ended without a completion status.");
                await Task.Delay(20, lifetime.Token);
            }
            lock (sync) state = "finishing";
        }
        catch (AddonException error) { Fail(error.Code, error.Message); }
        catch (OperationCanceledException) { Fail("output_cancelled", "Output was cancelled or exceeded a transport deadline."); }
        catch (Exception)
        { Fail("output_unavailable", "The approved service could not receive the encoded stream."); }
        finally
        {
            // Cancellation and producer release also unblock a pending read
            // from the anonymous pipe if its Windows handle is synchronous.
            lifetime.Cancel();
            try
            {
                await ReleaseProducerAsync();
                lock (sync) if (!closing) state = errorCode is null ? "completed" : "failed";
            }
            catch
            {
                lock (sync) { state = "failed"; errorCode = "cleanup_pending"; errorMessage = "Output resources still need cleanup. Close this session again to retry."; }
            }
        }
    }
    private void Fail(string code, string message)
    {
        lock (sync) { state = "finishing"; if (errorCode is null) { errorCode = code; errorMessage = message; } }
    }
    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            closing = true;
            if (cleanup is null || cleanup.IsFaulted) cleanup = CloseAsync();
            return new(cleanup);
        }
    }
    private async Task CloseAsync()
    {
        await Task.Yield();
        lifetime.Cancel();
        await ReleaseProducerAsync();
        try { await uploading.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) { throw new AddonException("cleanup_pending", "Output transport is still closing. Retry cleanup."); }
        lock (sync) state = "closed";
        lifetime.Dispose();
    }
    private async Task ReleaseProducerAsync()
    {
        await producer.DisposeAsync();
        var read = Volatile.Read(ref pendingRead);
        if (read is null) return;
        try { await read.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) { throw new AddonException("cleanup_pending", "Native output read is still closing. Retry cleanup."); }
        catch (Exception) { /* A failed/cancelled read has released its resources. */ }
    }

    private sealed class MediaContent(OutputUploadSession owner, Stream input) : HttpContent
    {
        private readonly Rate rate = new(BytesPerSecond);
        private readonly CancellationTokenSource earlyResponse = new();
        internal volatile bool Finished;
        internal void Stop() => earlyResponse.Cancel();
        protected override void Dispose(bool disposing) { if (disposing) earlyResponse.Dispose(); base.Dispose(disposing); }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => CopyAsync(stream, owner.lifetime.Token);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) => CopyAsync(stream, token);
        private async Task CopyAsync(Stream destination, CancellationToken token)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, owner.lifetime.Token, earlyResponse.Token);
            byte[] buffer = new byte[64 * 1024];
            while (true)
            {
                // A synchronous Windows anonymous pipe cannot interrupt its OS
                // read. Cancellation releases this await; producer cleanup in
                // RunAsync closes the writer and drains that one pending read.
                stop.Token.ThrowIfCancellationRequested();
                var read = input.ReadAsync(buffer, stop.Token).AsTask();
                Volatile.Write(ref owner.pendingRead, read);
                int count = await read.WaitAsync(stop.Token);
                if (count == 0) break;
                Contract.Require(Interlocked.Read(ref owner.bytes) + count <= MaximumBytes, "output_limit", "Encoded stream exceeded its byte limit.");
                await rate.TakeAsync(count, stop.Token); await GlobalRate.TakeAsync(count, stop.Token);
                using var write = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                write.CancelAfter(TimeSpan.FromSeconds(15));
                await destination.WriteAsync(buffer.AsMemory(0, count), write.Token);
                Interlocked.Add(ref owner.bytes, count);
                lock (owner.sync) if (!owner.closing && owner.state is "opening" or "running") owner.state = "running";
            }
            Finished = true;
            // The full stream was serialized; a silent service must not hold a
            // finished native output for the remaining 24-hour session limit.
            owner.lifetime.CancelAfter(TimeSpan.FromSeconds(15));
        }
    }
    private sealed class Rate(int maximum)
    {
        private readonly object gate = new();
        private long started = Stopwatch.GetTimestamp();
        private int bytes;
        public async Task TakeAsync(int count, CancellationToken token)
        {
            while (true)
            {
                TimeSpan delay;
                lock (gate)
                {
                    var elapsed = Stopwatch.GetElapsedTime(started);
                    if (elapsed >= TimeSpan.FromSeconds(1)) { started = Stopwatch.GetTimestamp(); bytes = 0; elapsed = TimeSpan.Zero; }
                    if (bytes + count <= maximum) { bytes += count; return; }
                    delay = TimeSpan.FromSeconds(1) - elapsed;
                }
                await Task.Delay(delay, token);
            }
        }
    }
}
