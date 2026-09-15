using System.Diagnostics;
using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons;

internal sealed class StreamMetrics
{
    private long previousWall;
    private double previousPosition;
    private double? speed, interval;
    internal JsonObject Update(JsonObject native, JsonObject counters, double sourceStart, double? publishedEnd, bool segmented, bool userPaused)
    {
        double? position = native["buffer"]?["producedEndSeconds"]?.GetValue<double>();
        bool paused = userPaused || native["buffer"]?["bufferPaused"]?.GetValue<bool>() == true || native["buffer"]?["storagePaused"]?.GetValue<bool>() == true;
        long now = Stopwatch.GetTimestamp();
        if (position is double current)
        {
            if (previousWall == 0 || paused) { previousWall = now; previousPosition = current; speed = interval = null; }
            else if (Stopwatch.GetElapsedTime(previousWall, now).TotalSeconds is double elapsed && elapsed >= 1)
            {
                speed = Math.Max(0, current - previousPosition) / elapsed; interval = elapsed;
                previousPosition = current; previousWall = now;
            }
        }
        double? mediaSeconds = (segmented ? publishedEnd : position) - sourceStart;
        long bytes = counters["bytesProduced"]?.GetValue<long>() ?? 0;
        return new() { ["processingMediaSecondsPerWallSecond"] = paused ? null : speed,
            ["speedMeasurementWallSeconds"] = paused ? null : interval,
            ["effectiveBitrateKbps"] = mediaSeconds > 0 && bytes > 0 ? bytes * 8 / mediaSeconds / 1000 : null,
            ["bitrateMeasurementMediaSeconds"] = mediaSeconds > 0 ? mediaSeconds : null,
            ["bitrateBasis"] = segmented ? "completedObjectsSinceStart" : "continuousBytesSinceStart" };
    }
}
