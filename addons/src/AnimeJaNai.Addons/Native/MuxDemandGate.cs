using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons.Native;

// Packet-boundary backpressure. Waiting here fills only the bounded private
// pipe; the control channel and unrelated streams continue independently.
internal sealed class MuxDemandGate(double sourceStart)
{
    private readonly object sync = new();
    private readonly double origin = sourceStart;
    private double demand = sourceStart, produced = sourceStart;
    private bool userPaused, bufferPaused, storagePaused;
    internal void StoragePause(bool paused) { lock (sync) storagePaused = paused; }
    internal void SetDemand(double position)
    {
        Contract.Require(double.IsFinite(position) && position >= origin, "invalid_position", "Invalid demand position.");
        lock (sync) { demand = position; Monitor.PulseAll(sync); }
    }
    internal void Pause(bool paused) { lock (sync) { userPaused = paused; Monitor.PulseAll(sync); } }
    internal void Admit(double start, double end, CancellationToken token)
    {
        Contract.Require(double.IsFinite(start) && double.IsFinite(end) && end >= start, "stream_timestamps_unavailable", "Encoded packets need a finite presentation timeline.");
        Contract.Require(end - start <= 15, "stream_timestamps_unavailable", "A single encoded packet exceeds the supported playback window.");
        end += origin;
        lock (sync)
        {
            for (;;)
            {
                token.ThrowIfCancellationRequested();
                if (end > demand + 15) bufferPaused = true;
                if (bufferPaused && produced - demand < 5 && end <= demand + 15) bufferPaused = false;
                if (!userPaused && !bufferPaused) { produced = Math.Max(produced, end); return; }
                Monitor.Wait(sync, 250);
            }
        }
    }
    internal JsonObject Status()
    {
        lock (sync) return new() { ["demandPositionSeconds"] = demand, ["producedEndSeconds"] = produced,
            ["bufferedSeconds"] = Math.Max(0, produced - demand), ["userPaused"] = userPaused, ["bufferPaused"] = bufferPaused, ["storagePaused"] = storagePaused };
    }
}
