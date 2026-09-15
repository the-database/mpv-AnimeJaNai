using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons;

public sealed record SceneRequest(int Width = 160, int Height = 90, int DeadlineMs = 25)
{
    public const int MaximumBytes = 320 * 180 * 2;
    public void Validate() => Contract.Require(Width is >= 1 and <= 320 && Height is >= 1 and <= 180 && DeadlineMs is >= 5 and <= 100,
        "invalid_request", "Scene samples must be 1..320 by 1..180 pixels with a 5..100 ms decision deadline.");
    public static SceneRequest Parse(JsonObject value)
    {
        long width = Contract.Number(value, "width"), height = Contract.Number(value, "height"), deadline = Contract.Number(value, "deadlineMs");
        Contract.Require(width is >= 1 and <= 320 && height is >= 1 and <= 180 && deadline is >= 5 and <= 100,
            "invalid_request", "Scene sample dimensions or deadline are outside the supported limits.");
        return new((int)width, (int)height, (int)deadline);
    }
}
public sealed record ScenePair(JsonObject Metadata, ReadOnlyMemory<byte> Pixels);
public interface ISceneSubscription : IDisposable
{
    ScenePair? Read();
    bool Submit(string requestId, int decision);
    JsonObject Status();
}
public interface ISceneSource : IDisposable
{
    ISceneSubscription Subscribe(SceneRequest request, Action ready);
}

// One exclusive detector per player. IDs and ownership are independent of
// normal frame observers; observing a player never grants processing control.
public sealed class SceneDetectionRegistry : IDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<string, ISceneSource> players = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Detector> detectors = new(StringComparer.Ordinal);
    private bool disposed;
    public sealed class Owner
    {
        internal readonly SceneDetectionRegistry Registry;
        internal readonly Action Wake;
        internal readonly Queue<string> Pending = new();
        internal readonly HashSet<string> Queued = new(StringComparer.Ordinal);
        internal bool Closed;
        internal Owner(SceneDetectionRegistry registry, Action wake) { Registry = registry; Wake = wake; }
    }
    private sealed record Detector(Owner Owner, string PlayerId, ISceneSubscription Subscription);
    public Owner CreateOwner(Action ready) { lock (sync) { Alive(); return new(this, ready); } }
    public IDisposable Register(string playerId, ISceneSource source)
    {
        lock (sync)
        {
            Alive(); Contract.Require(players.Count < 4 && !players.ContainsKey(playerId), "capacity_exceeded", "Scene player capacity is full.");
            players.Add(playerId, source); return new Registration(this, playerId);
        }
    }
    public JsonArray List(Owner owner)
    {
        lock (sync)
        {
            Check(owner);
            return new(players.Keys.Select(id => (JsonNode)new JsonObject { ["playerId"] = id,
                ["inUse"] = detectors.Values.Any(d => d.PlayerId == id), ["format"] = "gray8", ["stage"] = "beforeInterpolation" }).ToArray());
        }
    }
    public JsonObject Attach(Owner owner, string playerId, SceneRequest request)
    {
        request.Validate();
        lock (sync)
        {
            Check(owner);
            Contract.Require(players.TryGetValue(playerId, out var source), "player_not_found", "The selected player is no longer attached.");
            Contract.Require(!detectors.Values.Any(d => d.PlayerId == playerId), "scene_detector_in_use", "This player already has a scene detector.");
            Contract.Require(detectors.Values.Count(d => d.Owner == owner) < 4, "capacity_exceeded", "An addon can attach at most four scene detectors.");
            string id = Guid.NewGuid().ToString("N");
            var subscription = source.Subscribe(request, () => Notify(owner, id));
            detectors.Add(id, new(owner, playerId, subscription));
            return new() { ["detectorId"] = id, ["width"] = request.Width, ["height"] = request.Height,
                ["deadlineMs"] = request.DeadlineMs, ["format"] = "gray8", ["stage"] = "beforeInterpolation" };
        }
    }
    private void Notify(Owner owner, string id)
    {
        lock (sync)
        {
            if (disposed || owner.Closed || !detectors.ContainsKey(id) || owner.Pending.Count >= 8 || !owner.Queued.Add(id)) return;
            owner.Pending.Enqueue(id); owner.Wake();
        }
    }
    public JsonObject? TakeEvent(Owner owner)
    {
        lock (sync)
        {
            if (disposed || owner.Closed) return null;
            while (owner.Pending.TryDequeue(out var id))
            {
                owner.Queued.Remove(id);
                if (detectors.TryGetValue(id, out var detector) && detector.Owner == owner)
                    return new() { ["detectorId"] = id };
            }
            return null;
        }
    }
    public BrokerResponse Read(Owner owner, string id)
    {
        lock (sync)
        {
            var pair = Find(owner, id).Subscription.Read();
            if (pair is null) return new(new JsonObject { ["pair"] = null, ["byteLength"] = 0 });
            long width = Contract.Number(pair.Metadata, "width"), height = Contract.Number(pair.Metadata, "height");
            Contract.Require(width is >= 1 and <= 320 && height is >= 1 and <= 180 && pair.Pixels.Length == width * height * 2,
                "invalid_frame", "Invalid scene pair representation.");
            return new(new JsonObject { ["pair"] = pair.Metadata.DeepClone(), ["byteLength"] = pair.Pixels.Length }, pair.Pixels);
        }
    }
    public bool Submit(Owner owner, string id, string requestId, string decision)
    {
        int value = decision switch { "default" => -1, "continuous" => 0, "cut" => 1, _ => throw new AddonException("invalid_request", "Choose cut, continuous or default.") };
        lock (sync) return Find(owner, id).Subscription.Submit(requestId, value);
    }
    public JsonObject Status(Owner owner, string id) { lock (sync) return Find(owner, id).Subscription.Status(); }
    public void Detach(Owner owner, string id)
    {
        lock (sync)
        {
            var detector = Find(owner, id); detectors.Remove(id); owner.Queued.Remove(id); detector.Subscription.Dispose();
        }
    }
    public void ReleaseOwner(Owner owner)
    {
        lock (sync)
        {
            if (owner.Registry != this || owner.Closed) return;
            foreach (var pair in detectors.Where(d => d.Value.Owner == owner).ToArray()) Detach(owner, pair.Key);
            owner.Closed = true; owner.Pending.Clear(); owner.Queued.Clear();
        }
    }
    private Detector Find(Owner owner, string id)
    {
        Check(owner); Contract.Require(detectors.TryGetValue(id, out var detector) && detector.Owner == owner,
            "scene_detector_not_found", "Unknown scene detector.");
        return detector;
    }
    private void Check(Owner owner) { Alive(); Contract.Require(owner.Registry == this && !owner.Closed, "owner_closed", "Scene detector owner has closed."); }
    private void Alive() => Contract.Require(!disposed, "feature_unavailable", "Scene detection is unavailable.");
    private void Remove(string id)
    {
        lock (sync)
        {
            foreach (var pair in detectors.Where(d => d.Value.PlayerId == id).ToArray()) Detach(pair.Value.Owner, pair.Key);
            if (players.Remove(id, out var source)) source.Dispose();
        }
    }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            foreach (string id in players.Keys.ToArray()) Remove(id);
            disposed = true;
        }
    }
    private sealed class Registration(SceneDetectionRegistry owner, string id) : IDisposable { public void Dispose() => owner.Remove(id); }
}
