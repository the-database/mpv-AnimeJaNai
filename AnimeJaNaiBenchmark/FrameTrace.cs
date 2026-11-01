using System.Globalization;

namespace AnimeJaNaiBenchmark;

public sealed record FrameMeasurement(long Frames, double Seconds, double Fps,
    long FirstEventNs, long LastEventNs, double P95OutputIntervalMs, double MaxOutputIntervalMs);

public static class FrameTrace
{
    // Native VO completion events use the same monotonic clock as mp.get_time().
    // Counting intervals between endpoints avoids counting either boundary twice.
    public static FrameMeasurement Measure(IEnumerable<string> lines, double startSeconds, double endSeconds)
    {
        const string suffix = " end video-flip";
        long first = 0, previous = 0, frames = 0;
        var intervals = new List<double>();
        foreach (string line in lines)
        {
            if (!line.EndsWith(suffix, StringComparison.Ordinal) ||
                !long.TryParse(line.AsSpan(0, line.Length - suffix.Length), CultureInfo.InvariantCulture, out long ns))
                continue;
            double seconds = ns / 1_000_000_000.0;
            if (seconds < startSeconds || seconds > endSeconds) continue;
            if (previous == 0) { first = previous = ns; continue; }
            if (ns <= previous) throw new InvalidDataException("Frame output timestamps did not advance.");
            intervals.Add((ns - previous) / 1_000_000.0);
            previous = ns;
            frames++;
        }
        if (frames == 0) throw new InvalidDataException("Fewer than two frame output events in the measurement window.");
        double elapsed = (previous - first) / 1_000_000_000.0;
        intervals.Sort();
        return new(frames, elapsed, frames / elapsed, first, previous,
            intervals[(int)Math.Ceiling(intervals.Count * 0.95) - 1], intervals[^1]);
    }
}
