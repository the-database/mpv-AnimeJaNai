using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons.Native;

namespace AnimeJaNai.Addons;

internal sealed class StreamCache : IAsyncDisposable
{
    private static readonly SemaphoreSlim Capacity = new(4, 4);
    private static readonly object Creation = new();
    private readonly object sync = new();
    private readonly FileStream ownership;
    private readonly Dictionary<string, Resource> resources = new(StringComparer.Ordinal);
    private readonly Dictionary<long, Resource> segments = [];
    private readonly HashSet<Lease> readers = [];
    private readonly CancellationTokenSource lifetime = new();
    private bool closed, released;
    private string? initialization;
    private long producedBytes, servedBytes;
    private long lastPublished = -1;
    private Task? cleanup;
    private JsonObject? encodedMetadata;
    internal string Directory { get; }
    internal string Generation { get; } = Guid.NewGuid().ToString("N");
    internal double SourceStart { get; }
    private volatile bool producerCompleted;
    internal bool ProducerCompleted { get => producerCompleted; set => producerCompleted = value; }
    internal sealed record Resource(string Id, string FileName, string Path, long Length, string? Hash,
        long? Sequence, double? Start, double? End, bool Initialization = false, bool Continuous = false, double? TimestampOrigin = null)
    {
        internal JsonObject Describe(string generation, string? init) => new()
        {
            ["resourceId"] = Id, ["generationId"] = generation, ["sequence"] = Sequence,
            ["sourceStartSeconds"] = Start, ["sourceEndSeconds"] = End, ["durationSeconds"] = End - Start,
            ["encodedTimestampOriginSeconds"] = TimestampOrigin, ["byteLength"] = Length, ["independent"] = !Continuous,
            ["initializationId"] = init, ["etag"] = Hash is null ? null : '"' + Hash + '"',
        };
    }
    private StreamCache(string directory, FileStream ownership, double sourceStart)
    { Directory = directory; this.ownership = ownership; SourceStart = sourceStart; }
    internal static StreamCache Create(string dataRoot, double sourceStart)
    { lock (Creation) return CreateOwned(dataRoot, sourceStart); }
    private static StreamCache CreateOwned(string dataRoot, double sourceStart)
    {
        Recover(dataRoot);
        Contract.Require(Capacity.Wait(0), "capacity_exceeded", "Four retained stream caches reserve the host's 8 GiB storage allowance. Close a stream first.");
        string? path = null;
        try
        {
            path = WorkerBridge.CreateWorkDirectory(SafeFiles.DirectoryPath(dataRoot, "stream-cache"), "stream");
            var owner = new FileStream(Path.Combine(path, ".owner"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Delete);
            return new(path, owner, sourceStart);
        }
        catch
        {
            Capacity.Release();
            if (path is not null && !System.IO.Directory.EnumerateFileSystemEntries(path).Any()) System.IO.Directory.Delete(path);
            throw;
        }
    }
    internal static void Recover(string dataRoot)
    {
        string root = Path.GetFullPath(Path.Combine(dataRoot, "stream-cache"));
        foreach (string candidateRoot in new[] { root, WorkerBridge.ShortWorkRoot(root) }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            SafeFiles.CheckParents(candidateRoot);
            if (!System.IO.Directory.Exists(candidateRoot)) continue;
            foreach (string path in System.IO.Directory.EnumerateDirectories(candidateRoot, "stream-*"))
            {
                string name = Path.GetFileName(path);
                if (!Guid.TryParseExact(name[7..], "N", out _)) continue;
                SafeFiles.CheckParents(path);
                string marker = Path.Combine(path, ".owner");
                if (!File.Exists(marker)) continue; // A different host can still be creating its ownership marker.
                SafeFiles.CheckParents(marker);
                FileStream owner;
                try { owner = new(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete); }
                catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { continue; } // Live owner.
                catch (IOException) { throw new AddonException("cache_recovery_failed", "An old stream cache needs cleanup before allocating more storage."); }
                using (owner)
                {
                    try { DeleteOwnedDirectory(path); }
                    catch (IOException) { throw new AddonException("cache_recovery_failed", "An old stream cache needs cleanup before allocating more storage."); }
                }
            }
        }
    }
    internal async Task PublishAsync(NativeSegmentIndex index, CancellationToken token)
    {
        if (index.Initialization is string init && initialization is null)
        {
            var resource = await SnapshotAsync(init, null, null, null, true, token);
            if (resource is null) return;
            lock (sync) { CheckOpen(); resources.Add(resource.Id, resource); initialization = resource.Id; producedBytes += resource.Length; }
        }
        foreach (var segment in index.Segments)
        {
            lock (sync)
            {
                CheckOpen(); if (segment.Sequence <= lastPublished) continue;
                Contract.Require(segment.Sequence == lastPublished + 1, "segment_history_lost", "Native segment index skipped unpublished media.");
            }
            var resource = await SnapshotAsync(segment.FileName, segment.Sequence, SourceStart + segment.Start, SourceStart + segment.End, false, token);
            if (resource is null) break; // CSV can be published immediately before the native writer closes.
            lock (sync)
            {
                CheckOpen(); resources.Add(resource.Id, resource); segments.Add(segment.Sequence, resource); producedBytes += resource.Length; lastPublished = segment.Sequence;
            }
        }
    }
    private async Task<Resource?> SnapshotAsync(string name, long? sequence, double? start, double? end, bool init, CancellationToken token)
    {
        Contract.Require(NativeMuxer.AllowedFile(name) && Path.GetFileName(name) == name, "invalid_stream_resource", "Unexpected native resource.");
        string path = Path.Combine(Directory, name); SafeFiles.CheckParents(path);
        FileStream file;
        try { file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous); }
        catch (IOException) { return null; }
        await using (file)
        {
            var metadata = init ? null : EncodedMediaMetadata.Read(Directory, name);
            if (!init && metadata is null) return null;
            Contract.Require(file.Length is > 0 and <= NativeMuxer.MaximumBytes, "invalid_stream_resource", "Completed media object is empty or exceeds its quota.");
            string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token));
            if (metadata is not null) lock (sync) encodedMetadata ??= metadata;
            return new(Guid.NewGuid().ToString("N"), name, path, file.Length, hash, sequence, start, end, init, TimestampOrigin: EncodedMediaMetadata.Origin(metadata));
        }
    }
    internal void PublishContinuous(string container)
    {
        string name = container switch { "matroska" => "continuous.mkv", "mpegts" => "continuous.ts", _ => "continuous.mp4" };
        string path = Path.Combine(Directory, name); SafeFiles.CheckParents(path);
        lock (sync)
        {
            CheckOpen();
            encodedMetadata ??= EncodedMediaMetadata.Read(Directory, name);
            if (resources.Values.Any(r => r.Continuous) || !File.Exists(path) || new FileInfo(path).Length == 0) return;
            var resource = new Resource(Guid.NewGuid().ToString("N"), name, path, 0, null, null, SourceStart, null, Continuous: true);
            resources.Add(resource.Id, resource);
        }
    }
    internal JsonObject? EncodedMetadata() { lock (sync) return encodedMetadata?.DeepClone().AsObject(); }
    internal void Complete(NativeSegmentIndex? finalIndex)
    {
        lock (sync)
        {
            CheckOpen();
            Contract.Require(encodedMetadata is not null && resources.Values.Any(r => !r.Initialization),
                "stream_incomplete", "Native output completed without inspected media.");
            if (finalIndex is not null)
                Contract.Require(finalIndex.Segments.Length > 0 && finalIndex.Segments.All(s => s.Sequence <= lastPublished),
                    "stream_incomplete", "Native output completed before all indexed media became available.");
            ProducerCompleted = true;
        }
    }
    internal JsonObject List(string? cursor, int limit)
    {
        Contract.Require(limit is >= 1 and <= 32, "invalid_request", "List one to 32 segments per page.");
        long after = 0;
        if (cursor is not null)
        {
            Contract.Require(cursor.StartsWith(Generation + ":", StringComparison.Ordinal) && long.TryParse(cursor[(Generation.Length + 1)..], out after) && after >= 0,
                "stale_generation", "Segment cursor belongs to another generation or is invalid.");
        }
        lock (sync)
        {
            CheckOpen(); var page = segments.Values.Where(r => r.Sequence >= after).OrderBy(r => r.Sequence).Take(limit).ToArray();
            return new JsonObject { ["generationId"] = Generation, ["segments"] = new JsonArray(page.Select(r => (JsonNode)r.Describe(Generation, initialization)).ToArray()),
                ["initializationId"] = initialization, ["continuousResourceId"] = resources.Values.FirstOrDefault(r => r.Continuous)?.Id,
                ["nextCursor"] = Generation + ":" + (page.Length == 0 ? after : page[^1].Sequence + 1) };
        }
    }
    internal (double? Start, double? End) Range()
    {
        lock (sync) return segments.Count == 0 ? (null, null) : (segments.Values.Min(r => r.Start), segments.Values.Max(r => r.End));
    }
    internal void Expire(double position)
    {
        lock (sync)
        {
            CheckOpen();
            foreach (var resource in segments.Values.Where(r => r.End < position - 30 && readers.All(l => l.Resource.Id != r.Id)).ToArray())
            {
                SafeFiles.CheckParents(resource.Path); File.Delete(resource.Path);
                string metadata = Path.Combine(Directory, EncodedMediaMetadata.FileName(resource.FileName));
                SafeFiles.CheckParents(metadata); File.Delete(metadata);
                segments.Remove(resource.Sequence!.Value); resources.Remove(resource.Id);
            }
        }
    }
    internal JsonObject Counters()
    {
        lock (sync)
        {
            long continuous = resources.Values.Where(r => r.Continuous).Sum(r => new FileInfo(r.Path).Length);
            return new() { ["bytesProduced"] = producedBytes + continuous, ["bytesServed"] = Interlocked.Read(ref servedBytes), ["activeReaders"] = readers.Count,
                ["cachedBytes"] = resources.Values.Where(r => !r.Continuous).Sum(r => r.Length) + continuous, ["maximumCacheBytes"] = NativeMuxer.MaximumBytes };
        }
    }
    internal Lease Acquire(string generation, string resourceId)
    {
        lock (sync)
        {
            CheckOpen();
            Contract.Require(generation == Generation, "stale_generation", "Requested media belongs to a different stream generation.");
            Contract.Require(resources.TryGetValue(resourceId, out var resource), "segment_expired", "Media resource is unavailable, expired, or belongs to another stream.");
            Contract.Require(readers.Count < 16, "capacity_exceeded", "This stream already has 16 active readers.");
            SafeFiles.CheckParents(resource.Path);
            FileStream file;
            try { file = new FileStream(resource.Path, FileMode.Open, FileAccess.Read, resource.Continuous ? FileShare.ReadWrite : FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan); }
            catch (IOException) { throw new AddonException("stream_unavailable", "The media object cannot currently be opened."); }
            if (!resource.Continuous && file.Length != resource.Length) { file.Dispose(); throw new AddonException("stream_changed", "A completed media resource changed."); }
            var lease = new Lease(this, resource, file, lifetime.Token); readers.Add(lease); return lease;
        }
    }
    internal sealed class Lease : IDisposable
    {
        private readonly StreamCache owner;
        private int released;
        internal Resource Resource { get; }
        internal FileStream File { get; }
        internal CancellationTokenSource Stop { get; }
        internal readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Lease(StreamCache owner, Resource resource, FileStream file, CancellationToken token)
        {
            this.owner = owner; Resource = resource; File = file;
            Stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            Stop.CancelAfter(resource.Continuous ? TimeSpan.FromHours(24) : TimeSpan.FromMinutes(2));
        }
        internal void Served(int count) => Interlocked.Add(ref owner.servedBytes, count);
        internal bool Complete => owner.ProducerCompleted;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            File.Dispose(); Stop.Dispose();
            lock (owner.sync) owner.readers.Remove(this);
            Done.TrySetResult();
        }
    }
    private void CheckOpen() => Contract.Require(!closed, "stream_closed", "Stream cache is closing or closed.");
    public ValueTask DisposeAsync()
    {
        lock (sync) { if (cleanup is null || cleanup.IsFaulted) cleanup = CloseAsync(); return new(cleanup); }
    }
    private async Task CloseAsync()
    {
        await Task.Yield();
        Task[] pending;
        lock (sync) { if (released) return; closed = true; lifetime.Cancel(); pending = readers.Select(r => r.Done.Task).ToArray(); }
        await Task.WhenAll(pending);
        DeleteOwnedDirectory(Directory);
        ownership.Dispose();
        lifetime.Dispose();
        lock (sync)
        {
            resources.Clear(); segments.Clear();
            if (!released) { released = true; Capacity.Release(); }
        }
    }
    private static void DeleteOwnedDirectory(string directory)
    {
        string resolved = Path.GetFullPath(directory), name = Path.GetFileName(resolved);
        Contract.Require(name.StartsWith("stream-", StringComparison.Ordinal) && Guid.TryParseExact(name[7..], "N", out _), "invalid_stream_cache", "Refusing to remove an unowned cache.");
        SafeFiles.CheckParents(resolved);
        if (System.IO.Directory.Exists(resolved))
        {
            var entries = System.IO.Directory.EnumerateFileSystemEntries(resolved).ToArray();
            foreach (string entry in entries)
            {
                SafeFiles.CheckParents(entry);
                Contract.Require((File.GetAttributes(entry) & FileAttributes.Directory) == 0 &&
                    (Path.GetFileName(entry) == ".owner" || NativeMuxer.AllowedFile(Path.GetFileName(entry))), "invalid_stream_cache", "Unexpected object in an owned stream cache.");
            }
            // Validate every object before deleting any. Delete the ownership marker last.
            foreach (string entry in entries.OrderBy(p => Path.GetFileName(p) == ".owner")) File.Delete(entry);
            System.IO.Directory.Delete(resolved, recursive: false);
        }
    }
}
