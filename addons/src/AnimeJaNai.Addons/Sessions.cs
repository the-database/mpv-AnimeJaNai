using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace AnimeJaNai.Addons;

// Implemented by trusted AJN integration code. Addons receive opaque public ids,
// never provider handles, executable names, command lines, or raw file paths.
public interface IProcessingSession : IAsyncDisposable
{
    Task<JsonObject> GetStatusAsync(CancellationToken cancellationToken);
}

public interface IControllableProcessingSession : IProcessingSession
{
    Task PauseAsync(bool paused, CancellationToken cancellationToken);
    Task SeekAsync(double seconds, CancellationToken cancellationToken);
}

public interface IProcessingSelectionProvider
{
    JsonObject ListSelections();
}

// A provider can hand cleanup ownership back even if starting the resource
// failed. The registry retains the resource if cleanup also needs a retry.
public sealed class ProcessingSessionStartException(IProcessingSession session, Exception cause) : Exception("Processing session start needs cleanup.", cause)
{
    public IProcessingSession Session { get; } = session;
}

public interface IProcessingSessionProvider
{
    int ApiMinor => 0;
    bool SupportsFrames => false;
    Task<IProcessingSession> OpenAsync(string sourceId, string? profileId, CancellationToken cancellationToken);
    IProcessingSessionProvider ForAddon(AddonPackage package, PermissionGrant grant) => this;
}

public sealed class SessionRegistry(IProcessingSessionProvider provider, int totalLimit = 16, int perOwnerLimit = 4)
{
    public sealed class Owner
    {
        internal readonly CancellationTokenSource Lifetime = new();
        internal readonly TaskCompletionSource Drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Pending;
        internal bool Closing;
        internal readonly IProcessingSessionProvider Provider;
        internal Owner(IProcessingSessionProvider provider) { Provider = provider; }
    }
    private sealed record OwnedSession(Owner Owner, IProcessingSession Session)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Task? Closing;
        public string? CleanupError;
        public Dictionary<string, IFrameSubscription> Frames { get; } = new(StringComparer.Ordinal);
    }
    private readonly Dictionary<string, OwnedSession> sessions = new(StringComparer.Ordinal);
    private readonly object sync = new();
    private int pending;
    public Owner CreateOwner() => new(provider);
    public Owner CreateOwner(AddonPackage package, PermissionGrant grant) => new(provider.ForAddon(package, grant));
    public int CapabilityMinor(Owner owner) => owner.Provider.ApiMinor;
    public bool SupportsFrames(Owner owner) => owner.Provider.SupportsFrames;

    public async Task<string> SubscribeFramesAsync(Owner owner, string sessionId, FrameRequest request, CancellationToken token)
    {
        request.Validate();
        OwnedSession item;
        lock (sync) item = Owned(owner, sessionId);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, owner.Lifetime.Token);
        await item.Gate.WaitAsync(linked.Token);
        try
        {
            lock (sync)
            {
                _ = Owned(owner, sessionId);
                Contract.Require(item.Closing is null, "session_closed", "Session is closing.");
                Contract.Require(item.Frames.Count == 0, "capacity_exceeded", "This producer supports one sample subscription per session.");
            }
            Contract.Require(owner.Provider.SupportsFrames && item.Session is IFrameProcessingSession, "feature_unavailable", "This session does not offer frame samples.");
            var subscription = ((IFrameProcessingSession)item.Session).SubscribeFrames(request);
            try
            {
                lock (sync)
                {
                    _ = Owned(owner, sessionId);
                    string id = Guid.NewGuid().ToString("N");
                    item.Frames.Add(id, subscription);
                    return id;
                }
            }
            catch { subscription.Dispose(); throw; }
        }
        finally { item.Gate.Release(); }
    }

    public async Task<FramePacket?> ReadFrameAsync(Owner owner, string subscriptionId, bool unsubscribe, CancellationToken token)
    {
        OwnedSession item;
        lock (sync)
        {
            Contract.Require(!owner.Closing, "subscription_not_found", "Frame subscription is closed.");
            item = sessions.Values.FirstOrDefault(s => s.Owner == owner && s.Frames.ContainsKey(subscriptionId))
                ?? throw new AddonException("subscription_not_found", "Frame subscription is not owned by this addon instance.");
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, owner.Lifetime.Token);
        await item.Gate.WaitAsync(linked.Token);
        try
        {
            IFrameSubscription subscription;
            lock (sync)
            {
                Contract.Require(!owner.Closing && item.Closing is null && item.Frames.ContainsKey(subscriptionId), "subscription_not_found", "Frame subscription is closed.");
                subscription = item.Frames[subscriptionId];
            }
            if (!unsubscribe) return subscription.ReadLatest();
            subscription.Dispose();
            lock (sync) item.Frames.Remove(subscriptionId);
            return null;
        }
        finally { item.Gate.Release(); }
    }

    public JsonObject Selections(Owner owner)
    {
        lock (sync) Contract.Require(!owner.Closing, "owner_closed", "Addon instance has stopped.");
        return owner.Provider is IProcessingSelectionProvider selections ? selections.ListSelections()
            : throw new AddonException("feature_unavailable", "This provider does not offer selected media.");
    }

    public async Task<string> OpenAsync(Owner owner, string source, string? profile, CancellationToken cancellationToken)
    {
        lock (sync)
        {
            Contract.Require(!owner.Closing, "owner_closed", "Addon instance has stopped.");
            Contract.Require(sessions.Count + pending < totalLimit && sessions.Values.Count(s => s.Owner == owner) + owner.Pending < perOwnerLimit,
                "capacity_exceeded", "Processing session limit reached.");
            pending++; owner.Pending++;
        }
        IProcessingSession? created = null;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, owner.Lifetime.Token);
            created = await owner.Provider.OpenAsync(source, profile, linked.Token);
            string id = Guid.NewGuid().ToString("N");
            lock (sync)
            {
                linked.Token.ThrowIfCancellationRequested();
                Contract.Require(!owner.Closing, "owner_closed", "Addon instance has stopped.");
                sessions.Add(id, new(owner, created));
                created = null;
                return id;
            }
        }
        catch (ProcessingSessionStartException error)
        {
            created = error.Session;
            throw new AddonException("session_start", "Processing could not start. Its resource cleanup will be retried when the addon stops.");
        }
        finally
        {
            try
            {
                if (created is not null)
                {
                    try { await created.DisposeAsync(); }
                    catch
                    {
                        // A cancelled open can still produce a resource if its
                        // provider is late to observe cancellation. If release
                        // fails, retain ownership/capacity for cleanup retry.
                        lock (sync) sessions.Add(Guid.NewGuid().ToString("N"), new(owner, created));
                        throw;
                    }
                }
            }
            finally
            {
                lock (sync)
                {
                    pending--; owner.Pending--;
                    if (owner.Closing && owner.Pending == 0) owner.Drained.TrySetResult();
                }
            }
        }
    }

    public async Task<JsonObject> StatusAsync(Owner owner, string id, CancellationToken cancellationToken)
    {
        OwnedSession item;
        lock (sync)
        {
            item = Owned(owner, id);
            if (item.Closing is not null) return new() { ["state"] = item.CleanupError is null ? "closing" : "cleanup_failed", ["error"] = item.CleanupError };
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, owner.Lifetime.Token);
        await item.Gate.WaitAsync(linked.Token);
        try
        {
            lock (sync) _ = Owned(owner, id);
            return await item.Session.GetStatusAsync(linked.Token);
        }
        finally { item.Gate.Release(); }
    }

    public async Task CloseAsync(Owner owner, string id, CancellationToken cancellationToken)
    {
        OwnedSession item;
        lock (sync) item = Owned(owner, id);
        await CloseItemAsync(id, item, cancellationToken);
    }

    public void RequestClose(Owner owner, string id)
    {
        lock (sync)
        {
            var item = Owned(owner, id);
            if (item.Closing is { IsCompleted: false }) return;
            item.CleanupError = null;
            item.Closing = Task.Run(async () =>
            {
                try { await CloseItemAsync(id, item, CancellationToken.None); }
                catch { lock (sync) item.CleanupError = "Session cleanup needs a retry. Close this session again or stop the addon."; }
            });
        }
    }

    public async Task ControlAsync(Owner owner, string id, bool? paused, double? seconds, CancellationToken cancellationToken)
    {
        Contract.Require(paused.HasValue != seconds.HasValue && (seconds is null || double.IsFinite(seconds.Value) && seconds >= 0 && seconds <= 315576000),
            "invalid_request", "Choose one valid session control.");
        OwnedSession item;
        lock (sync) item = Owned(owner, id);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, owner.Lifetime.Token);
        await item.Gate.WaitAsync(linked.Token);
        try
        {
            lock (sync)
            {
                _ = Owned(owner, id);
                Contract.Require(item.Closing is null, "session_closed", "Session is closing.");
            }
            Contract.Require(item.Session is IControllableProcessingSession, "feature_unavailable", "Session controls are unavailable.");
            var control = (IControllableProcessingSession)item.Session;
            if (paused.HasValue) await control.PauseAsync(paused.Value, linked.Token);
            else await control.SeekAsync(seconds!.Value, linked.Token);
        }
        finally { item.Gate.Release(); }
    }

    private async Task CloseItemAsync(string id, OwnedSession item, CancellationToken cancellationToken)
    {
        await item.Gate.WaitAsync(cancellationToken);
        try
        {
            lock (sync) if (!sessions.ContainsKey(id)) return;
            KeyValuePair<string, IFrameSubscription>[] subscriptions;
            lock (sync) subscriptions = item.Frames.ToArray();
            foreach (var entry in subscriptions)
            {
                entry.Value.Dispose();
                lock (sync) item.Frames.Remove(entry.Key);
            }
            await item.Session.DisposeAsync();
            lock (sync) sessions.Remove(id);
        }
        finally { item.Gate.Release(); }
    }

    public async Task ReleaseOwnerAsync(Owner owner)
    {
        bool cancel;
        lock (sync)
        {
            cancel = !owner.Closing;
            owner.Closing = true;
            if (owner.Pending == 0) owner.Drained.TrySetResult();
        }
        if (cancel) owner.Lifetime.Cancel();
        await owner.Drained.Task;
        KeyValuePair<string, OwnedSession>[] owned;
        lock (sync) owned = sessions.Where(s => s.Value.Owner == owner).ToArray();
        var errors = await Task.WhenAll(owned.Select(async item =>
        {
            try { await CloseItemAsync(item.Key, item.Value, CancellationToken.None); return (Exception?)null; }
            catch (Exception error) { return error; }
        }));
        if (errors.Any(e => e is not null)) throw new AggregateException("Processing session cleanup failed.", errors.Where(e => e is not null).Cast<Exception>());
    }

    private OwnedSession Owned(Owner owner, string id)
    {
        Contract.Require(!owner.Closing && sessions.TryGetValue(id, out var item) && item.Owner == owner,
            "session_not_found", "Session is not owned by this addon instance.");
        return sessions[id];
    }
}

// Queue behavior for a future native frame producer. This is not a GPU capture
// implementation. It owns immutable copies and never waits for a slow consumer.
public sealed record FrameSample(string SessionId, long PresentationMicroseconds, int Width, int Height, ReadOnlyMemory<byte> Rgba8);

public sealed class LatestFrameQueue
{
    private readonly Channel<FrameSample> channel = Channel.CreateBounded<FrameSample>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleWriter = false,
        SingleReader = true,
        AllowSynchronousContinuations = false,
    });

    public bool Publish(string sessionId, long presentationMicroseconds, int width, int height, ReadOnlySpan<byte> rgba)
    {
        Contract.Require(width > 0 && height > 0 && width <= 320 && height <= 180 && rgba.Length == width * height * 4,
            "invalid_frame", "Invalid sample dimensions or payload.");
        return channel.Writer.TryWrite(new(sessionId, presentationMicroseconds, width, height, rgba.ToArray()));
    }
    public ValueTask<FrameSample> ReadAsync(CancellationToken cancellationToken) => channel.Reader.ReadAsync(cancellationToken);
    public void Complete() => channel.Writer.TryComplete();
}
