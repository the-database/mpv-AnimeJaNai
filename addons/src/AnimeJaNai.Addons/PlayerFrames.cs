using System.Globalization;
using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons;

// Trusted producer interface. ReadLatest on its subscription returns the current
// snapshot (including an unchanged frame), or null after invalidation. The
// registry owns per-consumer deduplication; no producer waits for a consumer.
public interface IPlayerFrameSource : IDisposable
{
    IFrameSubscription Subscribe(FrameRequest request);
}

public sealed class PlayerFrameRegistry(TimeProvider? timeProvider = null, bool supportsAnalysis = false) : IDisposable
{
    public bool SupportsAnalysis { get; } = supportsAnalysis;
    private readonly object sync = new();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, Player> players = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Reader> readers = new(StringComparer.Ordinal);
    private bool disposed;
    public const int MaximumPlayers = 4, MaximumReadersPerPlayer = 8, MaximumReadersPerOwner = 4;

    public sealed class Owner
    {
        internal bool Closed;
        internal readonly PlayerFrameRegistry Registry;
        internal Owner(PlayerFrameRegistry registry) { Registry = registry; }
    }
    private sealed class Player(IPlayerFrameSource source)
    {
        internal readonly IPlayerFrameSource Source = source;
        internal bool Alive = true;
        internal readonly Dictionary<string, Channel> Channels = new(StringComparer.Ordinal);
    }
    private sealed class Channel
    {
        internal IFrameSubscription? Samples;
        internal FrameRequest? Request;
        internal string? Failure;
    }
    private sealed class Reader(Owner owner, Player player, FrameRequest request)
    {
        internal readonly Owner Owner = owner;
        internal readonly Player Player = player;
        internal readonly FrameRequest Request = request;
        internal Channel Channel => Player.Channels[Request.Channel];
        internal long? Attempt;
        internal ulong LastId;
        internal string? Epoch;
    }
    public Owner CreateOwner() { lock (sync) { Alive(); return new(this); } }
    public IDisposable Register(IPlayerFrameSource source, out string id)
    {
        lock (sync)
        {
            Alive();
            Contract.Require(players.Count < MaximumPlayers, "capacity_exceeded", "Too many attached players.");
            id = Guid.NewGuid().ToString("N");
            var player = new Player(source);
            players.Add(id, player);
            return new Registration(this, id, player);
        }
    }
    public JsonArray List(Owner owner)
    {
        lock (sync)
        {
            Check(owner);
            return new(players.Keys.Select(id => {
                var item = new JsonObject { ["playerId"] = id, ["stage"] = "processed", ["format"] = "bgra8" };
                if (SupportsAnalysis) { item["sourceSamples"] = true; item["characterMask"] = true; }
                return (JsonNode)item;
            }).ToArray());
        }
    }
    public JsonObject Subscribe(Owner owner, string playerId, FrameRequest request)
    {
        request.Validate();
        lock (sync)
        {
            Check(owner);
            Contract.Require(request.Stage == "processed" || SupportsAnalysis, "feature_unavailable", "This player build does not support source observations.");
            Contract.Require(players.TryGetValue(playerId, out var player), "player_not_found", "The player is no longer attached.");
            Contract.Require(readers.Values.Count(r => r.Owner == owner) < MaximumReadersPerOwner &&
                readers.Values.Count(r => r.Player == player) < MaximumReadersPerPlayer,
                "capacity_exceeded", "Player sample subscription capacity is full.");
            string id = Guid.NewGuid().ToString("N");
            player.Channels.TryAdd(request.Channel, new());
            readers.Add(id, new(owner, player, request));
            try { Configure(player); }
            catch
            {
                readers.Remove(id);
                ConfigureAfterRemoval(player);
                throw;
            }
            var result = request.Describe(); result["subscriptionId"] = id;
            return result;
        }
    }
    public FramePacket? Read(Owner owner, string id)
    {
        lock (sync)
        {
            var reader = Find(owner, id);
            Contract.Require(reader.Player.Alive, "player_closed", "The observed player has closed.");
            Contract.Require(reader.Channel.Failure is null, "frame_unavailable", "The player sample producer is unavailable.");
            long now = clock.GetTimestamp();
            if (reader.Attempt is long previous && clock.GetElapsedTime(previous, now).TotalSeconds < 1.0 / reader.Request.MaxFps) return null;
            reader.Attempt = now;
            var frame = reader.Channel.Samples?.ReadLatest();
            if (frame is null) return null;
            var metadata = frame.Metadata;
            string epoch = Contract.Text(metadata, "epoch", 32);
            Contract.Require(ulong.TryParse(Contract.Text(metadata, "frameId", 32), NumberStyles.None, CultureInfo.InvariantCulture, out ulong frameId),
                "invalid_frame", "Invalid player frame identity.");
            if (reader.Epoch == epoch && reader.LastId == frameId) return null;
            metadata["skippedSamples"] = reader.Epoch == epoch && reader.LastId > 0 && frameId > reader.LastId ? Math.Min(frameId - reader.LastId - 1, int.MaxValue) : 0;
            metadata["subscriptionId"] = id;
            metadata["playerId"] = players.Single(p => p.Value == reader.Player).Key;
            var packet = Resize(frame, metadata, reader.Request);
            reader.LastId = frameId; reader.Epoch = epoch;
            return packet;
        }
    }
    public void Unsubscribe(Owner owner, string id)
    {
        lock (sync)
        {
            var reader = Find(owner, id);
            readers.Remove(id);
            if (reader.Player.Alive) ConfigureAfterRemoval(reader.Player);
        }
    }
    public JsonObject Status(Owner owner, string id)
    {
        lock (sync)
        {
            var reader = Find(owner, id);
            if (!reader.Player.Alive) return new() { ["state"] = "closed" };
            if (reader.Channel.Failure is not null) return new() { ["state"] = "failed", ["message"] = reader.Channel.Failure };
            return reader.Channel.Samples?.Status() ?? new() { ["state"] = "idle" };
        }
    }
    public void ReleaseOwner(Owner owner)
    {
        lock (sync)
        {
            if (owner.Registry != this || owner.Closed) return;
            owner.Closed = true;
            var owned = readers.Where(p => p.Value.Owner == owner).ToArray();
            foreach (var item in owned) readers.Remove(item.Key);
            foreach (var player in owned.Select(p => p.Value.Player).Where(p => p.Alive).Distinct()) ConfigureAfterRemoval(player);
        }
    }
    private void ConfigureAfterRemoval(Player player)
    {
        try { Configure(player); }
        catch (Exception) { foreach (var channel in player.Channels.Values) channel.Failure = "The player sample producer could not be reconfigured."; }
    }
    private void Configure(Player player)
    {
        foreach (var (key, channel) in player.Channels)
        {
            var requests = readers.Values.Where(r => r.Player == player && r.Request.Channel == key).Select(r => r.Request).ToArray();
            FrameRequest? desired = requests.Length == 0 ? null : requests[0] with {
                Width = requests.Max(r => r.Width), Height = requests.Max(r => r.Height), MaxFps = requests.Max(r => r.MaxFps) };
            if (desired == channel.Request && channel.Failure is null) continue;
            channel.Samples?.Dispose(); channel.Samples = null; channel.Request = null;
            try
            {
                if (desired is not null) channel.Samples = player.Source.Subscribe(desired);
                channel.Request = desired; channel.Failure = null;
            }
            catch { channel.Failure = "The player sample producer could not be configured."; throw; }
        }
    }
    private static FramePacket Resize(FramePacket frame, JsonObject metadata, FrameRequest request)
    {
        int width = (int)Contract.Number(metadata, "width"), height = (int)Contract.Number(metadata, "height");
        if (width == request.Width && height == request.Height) return new(metadata, frame.Pixels.Span, frame.Mask.Span);
        Contract.Require(width >= request.Width && height >= request.Height, "invalid_frame", "Player samples must meet the negotiated dimensions.");
        var input = frame.Pixels.Span;
        byte[] output = new byte[request.Width * request.Height * 4];
        byte[] mask = frame.Mask.IsEmpty ? [] : new byte[request.Width * request.Height * 2];
        for (int y = 0; y < request.Height; y++)
        {
            int sy = Math.Clamp((2 * y + 1) * height * 256 / (2 * request.Height) - 128, 0, (height - 1) * 256);
            int y0 = sy >> 8, y1 = Math.Min(y0 + 1, height - 1), fy = sy & 255;
            for (int x = 0; x < request.Width; x++)
            {
                int sx = Math.Clamp((2 * x + 1) * width * 256 / (2 * request.Width) - 128, 0, (width - 1) * 256);
                int x0 = sx >> 8, x1 = Math.Min(x0 + 1, width - 1), fx = sx & 255;
                for (int c = 0; c < 4; c++)
                {
                    int top = input[(y0 * width + x0) * 4 + c] * (256 - fx) + input[(y0 * width + x1) * 4 + c] * fx;
                    int bottom = input[(y1 * width + x0) * 4 + c] * (256 - fx) + input[(y1 * width + x1) * 4 + c] * fx;
                    output[(y * request.Width + x) * 4 + c] = (byte)((top * (256 - fy) + bottom * fy + 32768) >> 16);
                }
                if (mask.Length > 0)
                {
                    int At(int px, int py) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(frame.Mask.Span[((py * width + px) * 2)..]);
                    long top = At(x0, y0) * (256L - fx) + At(x1, y0) * (long)fx;
                    long bottom = At(x0, y1) * (256L - fx) + At(x1, y1) * (long)fx;
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(mask.AsSpan((y * request.Width + x) * 2),
                        (ushort)((top * (256 - fy) + bottom * fy + 32768) >> 16));
                }
            }
        }
        metadata["width"] = (long)request.Width; metadata["height"] = (long)request.Height; metadata["stride"] = request.Width * 4;
        if (metadata["analysis"] is JsonObject analysis) { analysis["width"] = request.Width; analysis["height"] = request.Height; analysis["stride"] = request.Width * 2; }
        return new(metadata, output, mask);
    }
    private Reader Find(Owner owner, string id)
    {
        Check(owner);
        Contract.Require(readers.TryGetValue(id, out var reader) && reader.Owner == owner, "subscription_not_found", "Unknown player sample subscription.");
        return reader;
    }
    private void Check(Owner owner)
    {
        Alive();
        Contract.Require(owner.Registry == this && !owner.Closed, "owner_closed", "Addon observation access has stopped.");
    }
    private void Alive() => Contract.Require(!disposed, "feature_unavailable", "Player observations are closed.");
    private void Remove(string id, Player player)
    {
        lock (sync)
        {
            if (!player.Alive) return;
            player.Alive = false; players.Remove(id);
            try { foreach (var channel in player.Channels.Values) channel.Samples?.Dispose(); }
            finally { player.Channels.Clear(); player.Source.Dispose(); }
        }
    }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            List<Exception> errors = [];
            foreach (var pair in players.ToArray())
            {
                try { Remove(pair.Key, pair.Value); }
                catch (Exception error) { errors.Add(error); }
            }
            readers.Clear();
            if (errors.Count > 0) throw new AggregateException("Some player observations could not be closed.", errors);
        }
    }
    private sealed class Registration(PlayerFrameRegistry owner, string id, Player player) : IDisposable
    {
        public void Dispose() => owner.Remove(id, player);
    }
}
