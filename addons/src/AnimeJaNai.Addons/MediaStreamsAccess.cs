using System.Diagnostics;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons.Native;

namespace AnimeJaNai.Addons;

internal sealed class MediaStreamsAccess(PermissionGrant grant, IMediaStreamProvider provider, string dataRoot,
    HttpServerAccess servers, RequestCredentials credentials) : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<string, Item> streams = new(StringComparer.Ordinal);
    private bool disposed;
    private sealed class Item(StreamCache cache, StreamRequest options, RequestCredentials.Lease? credential, RequestCredentials.Lease? subtitleCredential)
    {
        internal readonly string Id = Guid.NewGuid().ToString("N"), SessionId = Guid.NewGuid().ToString("N");
        internal readonly StreamCache Cache = cache;
        internal readonly StreamRequest Options = options;
        internal readonly RequestCredentials.Lease? Credential = credential;
        internal readonly RequestCredentials.Lease? SubtitleCredential = subtitleCredential;
        internal readonly CancellationTokenSource Lifetime = CancellationTokenSource.CreateLinkedTokenSource(credential?.Token ?? default, subtitleCredential?.Token ?? default);
        internal readonly long Created = Stopwatch.GetTimestamp();
        internal long LastUsed = Stopwatch.GetTimestamp(), PausedAt;
        internal double Demand = options.Playback.StartSeconds;
        internal bool UserPaused, Closing, Closed, NativeReleased;
        internal string State = "opening";
        internal JsonObject NativeStatus = new(), Counters = new();
        internal JsonObject Metrics = new();
        internal readonly StreamMetrics Measurements = new();
        internal string? Error;
        internal IProcessingSession? Producer;
        internal Task Work = Task.CompletedTask;
        internal Task? Cleanup;
    }
    internal JsonObject Formats()
    {
        Demand(); return new()
        {
            ["modes"] = new JsonArray("segments", "continuous"), ["containers"] = new JsonArray("matroska", "mpegts", "fragmentedMp4"),
            ["maximumStreams"] = 4, ["maximumHostStreams"] = 4, ["maximumCacheBytes"] = NativeMuxer.MaximumBytes,
            ["maximumHostCacheBytes"] = 8L << 30, ["maximumReaders"] = 16, ["segmentReaderSeconds"] = 120,
            ["ioTimeoutSeconds"] = 15, ["minimumSegmentSeconds"] = .5, ["maximumSegmentSeconds"] = 6,
            ["storagePressureSeconds"] = 15,
            ["defaultSegmentSeconds"] = 1, ["retainBehindSeconds"] = 30, ["produceAheadSeconds"] = 15, ["resumeAheadSeconds"] = 5,
            ["abandonmentSeconds"] = 600, ["maximumPauseSeconds"] = 7200, ["maximumLifetimeSeconds"] = 86400,
            ["requires"] = "Matching native streaming runtime, an approved profile, prepared engines and a supported encoder",
            ["readinessVersion"] = provider.SupportsReadiness ? 1 : 0, ["encoders"] = new JsonArray("auto", "nvenc", "amf"),
            ["bitDepths"] = new JsonArray(8, 10), ["defaultBitDepth"] = 8,
            ["sourceLimits"] = new JsonObject { ["maximumWidth"] = 8192, ["maximumHeight"] = 8192, ["maximumPixels"] = 3840 * 2160, ["progressiveSdrOnly"] = true },
            ["hardwareQualified"] = false,
        };
    }
    internal bool SupportsReadiness => provider.SupportsReadiness;
    internal JsonObject Open(ProbeRequest source, string? profile, StreamRequest options)
    {
        lock (sync)
        {
            Demand(); grant.Demand("media.input");
            if (source.Remote is not null) grant.Demand("network.connect");
            Contract.Require((source.SourceId is null) != (source.Remote is null), "invalid_source", "Choose one approved stream source.");
            options.Validate();
            Contract.Require(streams.Values.Count(i => !i.Closed) < 4, "capacity_exceeded", "Close a retained stream before opening another.");
            RequestCredentials.Lease? credential = source.Remote?.CredentialId is string id ? credentials.Acquire(id, source.Remote.DestinationId) : null;
            RequestCredentials.Lease? subtitleCredential = null;
            StreamCache cache;
            try
            {
                if (options.Playback.ExternalRemoteSource is { } remote)
                { grant.Demand("network.connect"); if (remote.CredentialId is string subtitleId) subtitleCredential = credentials.Acquire(subtitleId, remote.DestinationId); }
                cache = StreamCache.Create(dataRoot, options.Playback.StartSeconds);
            }
            catch { credential?.Dispose(); subtitleCredential?.Dispose(); throw; }
            var item = new Item(cache, options, credential, subtitleCredential); streams.Add(item.Id, item);
            foreach (var old in streams.Values.Where(i => i.Closed).OrderBy(i => i.Created).Take(Math.Max(0, streams.Count - 128)).ToArray()) streams.Remove(old.Id);
            item.Work = Task.Run(() => RunAsync(item, source, profile));
            return new() { ["sessionId"] = item.SessionId, ["streamId"] = item.Id, ["generationId"] = cache.Generation };
        }
    }
    private async Task RunAsync(Item item, ProbeRequest source, string? profile)
    {
        var index = new SegmentIndex(item.Options.Encoding.Container);
        bool? sentPause = null; double? sentDemand = null;
        try
        {
            item.Producer = await provider.OpenStreamAsync(source, profile, item.Options, item.Cache.Directory, item.Lifetime.Token);
            while (true)
            {
                item.Lifetime.Token.ThrowIfCancellationRequested();
                Contract.Require(item.Options.NativeMode == "required" || item.NativeReleased || Stopwatch.GetElapsedTime(item.Created) < TimeSpan.FromMinutes(20),
                    "preparation_timeout", "Native preparation exceeded its bounded lifetime.");
                double demand; bool paused; long lastUsed, pausedAt;
                lock (sync) { demand = item.Demand; paused = item.UserPaused; lastUsed = item.LastUsed; pausedAt = item.PausedAt; }
                Contract.Require(Stopwatch.GetElapsedTime(item.Created) < TimeSpan.FromHours(24) &&
                    Stopwatch.GetElapsedTime(lastUsed) < TimeSpan.FromMinutes(10) &&
                    (!paused || Stopwatch.GetElapsedTime(pausedAt) < TimeSpan.FromHours(2)), "stream_expired", "Stream ownership updates or playback exceeded the supported lifetime.");
                if (item.Producer is not null)
                {
                    JsonObject state = await item.Producer.GetStatusAsync(item.Lifetime.Token);
                    string nativeState = state["state"]?.GetValue<string>() ?? "opening";
                    lock (sync) { item.NativeStatus = state; item.State = paused ? "paused" :
                        state["buffer"]?["bufferPaused"]?.GetValue<bool>() == true || state["buffer"]?["storagePaused"]?.GetValue<bool>() == true ? "bufferPaused" : nativeState; }
                    Contract.Require(nativeState != "failed", state["errorCode"]?.GetValue<string>() ?? "native_stream_failed", "Native processing did not complete successfully.");
                    if (item.Options.Segmented) await item.Cache.PublishAsync(index.Read(item.Cache.Directory), item.Lifetime.Token);
                    else item.Cache.PublishContinuous(item.Options.Encoding.Container);
                    if (nativeState == "completed")
                    {
                        await item.Producer.DisposeAsync(); item.Producer = null;
                        // Re-read after disposal: CSV indexes can precede the final writer close.
                        if (item.Options.Segmented) await item.Cache.PublishAsync(index.Read(item.Cache.Directory), item.Lifetime.Token);
                        else item.Cache.PublishContinuous(item.Options.Encoding.Container);
                        if (item.Options.NativeMode == "required") item.Cache.Complete(item.Options.Segmented ? index.Read(item.Cache.Directory) : null);
                        lock (sync) { item.NativeReleased = true; item.State = item.Options.NativeMode == "required" ? "producerCompleted" : "ready"; }
                    }
                    else
                    {
                        Contract.Require(item.Producer is IMediaStreamProducer, "native_protocol", "Stream provider lacks demand controls.");
                        var producer = (IMediaStreamProducer)item.Producer;
                        if (item.Options.NativeMode == "required") {
                            if (sentDemand != demand) { await producer.SetDemandAsync(demand, item.Lifetime.Token); sentDemand = demand; }
                            if (sentPause != paused) { await producer.PauseAsync(paused, item.Lifetime.Token); sentPause = paused; }
                        }
                    }
                }
                item.Cache.Expire(demand);
                var counters = item.Cache.Counters();
                lock (sync)
                {
                    item.Counters = counters;
                    item.Metrics = item.Measurements.Update(item.NativeStatus, counters, item.Options.Playback.StartSeconds, item.Cache.Range().End, item.Options.Segmented, paused, item.NativeReleased);
                }
                await Task.Delay(100, item.Lifetime.Token);
            }
        }
        catch (ProcessingSessionStartException error)
        { item.Producer = error.Session; lock (sync) item.Error = "native_start_failed"; }
        catch (OperationCanceledException) when (item.Lifetime.IsCancellationRequested)
        { lock (sync) if (!item.Closing) item.Error = "stream_revoked"; }
        catch (Exception error)
        { lock (sync) item.Error = error is AddonException addon ? addon.Code : "stream_failed"; }
        finally
        {
            item.Lifetime.Cancel();
            try { await CleanupAsync(item); }
            catch { lock (sync) { item.State = "cleanupFailed"; item.Error ??= "cleanup_failed"; } }
        }
    }
    private async Task CleanupAsync(Item item)
    {
        lock (sync) item.State = "closing";
        if (item.Producer is not null) { await item.Producer.DisposeAsync(); item.Producer = null; }
        lock (sync) item.NativeReleased = true;
        await item.Cache.DisposeAsync(); item.Credential?.Dispose(); item.SubtitleCredential?.Dispose();
        lock (sync)
        {
            // Closed status entries can outlive their credential context. Release
            // its linked registrations now, including entries later evicted from
            // the bounded history, rather than retaining them until addon stop.
            item.Lifetime.Dispose(); item.Closed = true;
            item.State = item.Error is null ? "closed" : "failed"; item.Counters = new();
        }
    }
    internal JsonObject Status(string streamId)
    {
        lock (sync)
        {
            var item = Find(streamId); var range = item.Closed ? (Start: (double?)null, End: (double?)null) : item.Cache.Range();
            return new() { ["streamId"] = item.Id, ["sessionId"] = item.SessionId, ["generationId"] = item.Cache.Generation,
                ["state"] = item.State, ["nativeCapacityReleased"] = item.NativeReleased, ["requestedStartSeconds"] = item.Options.Playback.StartSeconds,
                ["operation"] = item.Options.NativeMode, ["ready"] = item.Options.NativeMode != "required" && item.State == "ready",
                ["demandPositionSeconds"] = item.Demand, ["userPaused"] = item.UserPaused,
                ["retainedStartSeconds"] = range.Start, ["retainedEndSeconds"] = range.End,
                ["native"] = item.NativeStatus.DeepClone(), ["transfer"] = item.Counters.DeepClone(),
                ["measurements"] = item.Metrics.DeepClone(),
                ["encodedMedia"] = item.Cache.EncodedMetadata(),
                ["error"] = item.Error is null ? null : new JsonObject { ["code"] = item.Error, ["message"] = "Stream stopped or needs cleanup. Inspect its state before retrying." } };
        }
    }
    internal JsonObject Segments(string id, string? cursor, int limit)
    { lock (sync) return Active(id).Cache.List(cursor, limit); }
    internal void SetDemand(string id, double position)
    {
        lock (sync)
        {
            var item = Active(id); var range = item.Cache.Range();
            double end = item.NativeStatus["buffer"]?["producedEndSeconds"]?.GetValue<double>() ?? item.Options.Playback.StartSeconds;
            double start = range.Start ?? item.Options.Playback.StartSeconds;
            Contract.Require(double.IsFinite(position) && position >= start && position <= end, "stream_replacement_required", "Position is outside the available timeline. Close this output and reopen at the desired offset.");
            item.Demand = position; item.LastUsed = Stopwatch.GetTimestamp();
        }
    }
    internal void Pause(string id, bool paused)
    {
        lock (sync)
        {
            var item = Active(id); if (paused && !item.UserPaused) item.PausedAt = Stopwatch.GetTimestamp();
            item.UserPaused = paused; item.LastUsed = Stopwatch.GetTimestamp();
        }
    }
    internal void Serve(string requestId, string id, string generation, string resourceId)
    {
        Item item; StreamCache.Lease lease;
        lock (sync) { item = Active(id); lease = item.Cache.Acquire(generation, resourceId); item.LastUsed = Stopwatch.GetTimestamp(); }
        try
        {
            Task done = servers.ClaimNative(requestId, async (context, token) =>
            {
                using var scope = CancellationTokenSource.CreateLinkedTokenSource(token, item.Lifetime.Token);
                try { await StreamHttpDelivery.SendAsync(context, lease, item.Options.Encoding.Container, scope.Token); }
                finally { lease.Dispose(); }
            });
            _ = done.ContinueWith(t => { _ = t.Exception; lease.Dispose(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch { lease.Dispose(); throw; }
    }
    internal void RequestClose(string id)
    {
        lock (sync)
        {
            var item = Find(id); if (item.Closed) return;
            item.Closing = true; item.Lifetime.Cancel();
            if (item.Work.IsCompleted && (item.Cleanup is null || item.Cleanup.IsFaulted)) item.Cleanup = CleanupAsync(item);
        }
    }
    internal string? FindSession(string sessionId)
    { lock (sync) return streams.Values.FirstOrDefault(i => i.SessionId == sessionId)?.Id; }
    internal async Task CloseAsync(string id, CancellationToken token)
    {
        RequestClose(id);
        Task work; lock (sync) { var item = Find(id); work = item.Cleanup ?? item.Work; }
        // Canceling this wait does not cancel cleanup or release its capacity early.
        await work.WaitAsync(token);
        lock (sync) Contract.Require(Find(id).Closed, "cleanup_failed", "Stream cleanup needs another close request.");
    }
    private Item Find(string id)
    {
        Demand(); Contract.Require(streams.TryGetValue(id, out var item), "stream_not_found", "Stream belongs to another addon instance or is no longer available."); return item;
    }
    private Item Active(string id)
    {
        var item = Find(id); Contract.Require(!item.Closing && !item.Closed && item.Error is null && !item.Lifetime.IsCancellationRequested,
            "stream_closed", "Stream is closing, failed, or closed."); return item;
    }
    private void Demand()
    {
        grant.Demand("sessions.manage"); grant.Demand("media.output");
        Contract.Require(provider.SupportsStreams, "feature_unavailable", "The matching native stream runtime is unavailable.");
        Contract.Require(!disposed, "owner_closed", "Addon has stopped.");
    }
    public async ValueTask DisposeAsync()
    {
        Item[] items;
        lock (sync) { disposed = true; items = streams.Values.ToArray(); foreach (var item in items.Where(i => !i.Closed)) { item.Closing = true; item.Lifetime.Cancel(); } }
        await Task.WhenAll(items.Select(i => i.Work));
        foreach (var item in items)
        {
            if (item.Cleanup is not null) { try { await item.Cleanup; } catch { /* Retry owned cleanup below. */ } }
            if (!item.Closed) await CleanupAsync(item);
            item.Lifetime.Dispose();
        }
    }
}
