using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons;

// Timers generate bounded host events. They do not grant threads, native timer
// handles or concurrent guest callbacks. Missed ticks are coalesced, not queued.
public sealed class AddonTimers(TimeProvider? timeProvider = null)
{
    public sealed record Ticket(string Id, long Generation);
    private sealed record Entry(string Id, long Generation, long Start, long Due, long Period, bool Repeat);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly object sync = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim changed = new(0, 1);
    private long generation;
    private bool closed;

    public void Set(string id, int intervalMs, bool repeat)
    {
        Contract.Require(Contract.ValidKey(id) && intervalMs is >= 16 and <= 3_600_000, "invalid_request", "Timer id or interval is invalid; use 16..3600000 milliseconds.");
        lock (sync)
        {
            Contract.Require(!closed, "owner_closed", "Addon has stopped.");
            Contract.Require(entries.ContainsKey(id) || entries.Count < 8, "capacity_exceeded", "An addon can have at most eight timers.");
            long now = clock.GetTimestamp(), period = checked((long)(intervalMs / 1000.0 * clock.TimestampFrequency));
            entries[id] = new(id, ++generation, now, checked(now + period), period, repeat);
            Signal();
        }
    }
    public void Clear(string id)
    {
        Contract.Require(Contract.ValidKey(id), "invalid_request", "Invalid timer id.");
        lock (sync) { entries.Remove(id); Signal(); }
    }
    public Ticket? Due()
    {
        lock (sync)
        {
            var next = entries.Values.MinBy(e => e.Due);
            return next is not null && next.Due <= clock.GetTimestamp() ? new(next.Id, next.Generation) : null;
        }
    }
    // Recheck after obtaining the worker's event gate. A settings/action callback
    // can replace or cancel a timer while the scheduler is waiting for that gate.
    public JsonObject? Take(Ticket ticket)
    {
        lock (sync)
        {
            long now = clock.GetTimestamp();
            if (closed || !entries.TryGetValue(ticket.Id, out var entry) || entry.Generation != ticket.Generation || entry.Due > now) return null;
            long missed = Math.Max(0, (now - entry.Due) / entry.Period);
            if (entry.Repeat) entries[ticket.Id] = entry with { Due = checked(now + entry.Period) };
            else entries.Remove(ticket.Id);
            return new() { ["timerId"] = ticket.Id, ["elapsedMs"] = clock.GetElapsedTime(entry.Start, now).TotalMilliseconds,
                ["missedTicks"] = Math.Min(missed, int.MaxValue) };
        }
    }
    public Task WaitAsync(TimeSpan maximum, CancellationToken token)
    {
        TimeSpan delay;
        lock (sync)
        {
            var next = entries.Values.MinBy(e => e.Due);
            delay = next is null ? maximum : TimeSpan.FromTicks(Math.Clamp(clock.GetElapsedTime(clock.GetTimestamp(), next.Due).Ticks, 0, maximum.Ticks));
        }
        return changed.WaitAsync(delay, token);
    }
    public void Close() { lock (sync) { closed = true; entries.Clear(); Signal(); } }
    internal void Notify() { lock (sync) { if (!closed) Signal(); } }
    private void Signal() { if (changed.CurrentCount == 0) changed.Release(); }
}
