using System.Globalization;

namespace AnimeJaNai.Addons.Native;

internal sealed record NativeSegment(long Sequence, string FileName, double Start, double End);
internal sealed record NativeSegmentIndex(NativeSegment[] Segments, string? Initialization, bool Complete);

// Reads only fixed host-owned muxer filenames. The guest receives opaque IDs,
// never these paths or a URL that could skip per-request authorization.
internal sealed class SegmentIndex(string container)
{
    private long nextSequence;
    private double nextStart;
    private readonly Dictionary<long, NativeSegment> known = [];
    internal NativeSegmentIndex Read(string directory)
    {
        string path = Path.Combine(directory, container == "matroska" ? "index.csv" : "index.m3u8");
        if (!File.Exists(path)) return new([], null, false);
        string text;
        try
        {
            SafeFiles.CheckParents(path);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Contract.Require(file.Length <= 128 * 1024, "invalid_segment_index", "Native segment index exceeds its limit.");
            using var reader = new StreamReader(file); text = reader.ReadToEnd();
        }
        catch (FileNotFoundException) { return new([], null, false); }
        return Parse(text);
    }
    internal NativeSegmentIndex Parse(string text)
    {
        string[] lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Contract.Require(lines.Length <= 1024, "invalid_segment_index", "Native segment index exceeds its entry limit.");
        var entries = new List<NativeSegment>(); string? initialization = null;
        long sequence = -1; double? duration = null; bool complete = false;
        foreach (string line in lines)
        {
            if (container == "matroska")
            {
                string[] fields = line.Split(',');
                Contract.Require(fields.Length == 3, "invalid_segment_index", "Invalid native Matroska index.");
                string file = fields[0].Trim('"'); long number = FileSequence(file, "mkv");
                double start = Number(fields[1]), end = Number(fields[2]);
                Contract.Require(end > start && end - start <= 30, "invalid_segment_index", "Invalid native segment timestamps.");
                entries.Add(new(number, file, start, end)); continue;
            }
            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
            {
                Contract.Require(long.TryParse(line[22..], NumberStyles.None, CultureInfo.InvariantCulture, out sequence) && sequence >= 0,
                    "invalid_segment_index", "Invalid native media sequence.");
            }
            else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal)) duration = Number(line[8..].Split(',')[0]);
            else if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                Contract.Require(line == "#EXT-X-MAP:URI=\"init.mp4\"", "invalid_segment_index", "Unexpected native initialization object."); initialization = "init.mp4";
            }
            else if (line == "#EXT-X-ENDLIST") complete = true;
            else if (!line.StartsWith('#'))
            {
                long number = FileSequence(line, container == "fragmentedMp4" ? "m4s" : "ts");
                Contract.Require(duration is > 0 and <= 30 && number == sequence, "invalid_segment_index", "Invalid native segment sequence or duration.");
                if (!known.TryGetValue(number, out var segment))
                {
                    Contract.Require(number == nextSequence, "segment_history_lost", "The native producer outran its bounded segment index.");
                    segment = new(number, line, nextStart, nextStart + duration.Value);
                    known.Add(number, segment); nextSequence++; nextStart = segment.End;
                }
                else Contract.Require(segment.FileName == line && Math.Abs(segment.End - segment.Start - duration.Value) < .00001,
                    "invalid_segment_index", "A published segment changed its duration.");
                entries.Add(segment); sequence++; duration = null;
            }
        }
        Contract.Require(duration is null && entries.Count <= 128, "invalid_segment_index", "Incomplete or oversized native segment index.");
        for (int i = 1; i < entries.Count; i++)
            Contract.Require(entries[i].Sequence == entries[i - 1].Sequence + 1 && entries[i].Start >= entries[i - 1].Start,
                "invalid_segment_index", "Native segment ordering changed.");
        if (entries.Count > 0)
            foreach (long old in known.Keys.Where(k => k < entries[0].Sequence).ToArray()) known.Remove(old);
        return new(entries.ToArray(), initialization, complete);
    }
    private static double Number(string value)
    {
        Contract.Require(double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number) && number >= 0,
            "invalid_segment_index", "Invalid native timestamp."); return number;
    }
    private static long FileSequence(string name, string extension)
    {
        Contract.Require(name.Length == 14 + extension.Length && name.StartsWith("part_", StringComparison.Ordinal) && name.EndsWith('.' + extension, StringComparison.Ordinal) &&
            long.TryParse(name.AsSpan(5, 8), NumberStyles.None, CultureInfo.InvariantCulture, out _), "invalid_segment_index", "Unexpected native segment object.");
        return long.Parse(name.AsSpan(5, 8), CultureInfo.InvariantCulture);
    }
}
