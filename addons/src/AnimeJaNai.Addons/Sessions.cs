using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace AnimeJaNai.Addons;

// Implemented by trusted AJN integration code. Addons receive opaque public ids,
// never provider handles, executable names, command lines, or raw file paths.
public interface IProcessingSession : IAsyncDisposable
{
    Task<JsonObject> GetStatusAsync(CancellationToken cancellationToken);
}

public interface IProcessingSessionProvider
{
    Task<IProcessingSession> OpenAsync(string sourceId, string? profileId, CancellationToken cancellationToken);
}

public sealed class SessionRegistry(IProcessingSessionProvider provider, int totalLimit = 16, int perOwnerLimit = 4)
{
    public sealed class Owner
    {
        internal readonly CancellationTokenSource Lifetime = new();
        internal readonly TaskCompletionSource Drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Pending;
        internal bool Closing;
        internal Owner() { }
    }
    private sealed record OwnedSession(Owner Owner, IProcessingSession Session)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
    private readonly Dictionary<string, OwnedSession> sessions = new(StringComparer.Ordinal);
    private readonly object sync = new();
    private int pending;
    public Owner CreateOwner() => new();

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
            created = await provider.OpenAsync(source, profile, linked.Token);
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
        lock (sync) item = Owned(owner, id);
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

    private async Task CloseItemAsync(string id, OwnedSession item, CancellationToken cancellationToken)
    {
        await item.Gate.WaitAsync(cancellationToken);
        try
        {
            lock (sync) if (!sessions.ContainsKey(id)) return;
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
        List<Exception> errors = [];
        foreach (var item in owned)
        {
            try { await CloseItemAsync(item.Key, item.Value, CancellationToken.None); }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count != 0) throw new AggregateException("Processing session cleanup failed.", errors);
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
