using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons.Native;

// Private host-to-worker instruction. No path is accepted from addon code.
internal sealed record ServedOutputPlan(string Directory, string Container, bool Segmented, double SegmentSeconds = 1)
{
    internal void Validate()
    {
        Contract.Require(Path.IsPathFullyQualified(Directory) && Container is "matroska" or "mpegts" or "fragmentedMp4" &&
            double.IsFinite(SegmentSeconds) && SegmentSeconds is >= .5 and <= 6, "invalid_stream", "Invalid private served-output plan.");
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Directory));
        Contract.Require(name.StartsWith("stream-", StringComparison.Ordinal) && Guid.TryParseExact(name[7..], "N", out _),
            "invalid_stream_cache", "Output cache must be a host-owned stream directory.");
        SafeFiles.CheckParents(Directory);
    }
    internal JsonObject ToJson() => new() { ["directory"] = Directory, ["container"] = Container, ["segmented"] = Segmented, ["segmentSeconds"] = SegmentSeconds };
    internal static ServedOutputPlan Parse(JsonObject value)
    {
        Contract.Require(value["segmented"] is JsonValue flag && flag.TryGetValue<bool>(out _) &&
            value["segmentSeconds"] is JsonValue seconds && seconds.TryGetValue<double>(out _), "invalid_stream", "Invalid private output mode.");
        var result = new ServedOutputPlan(Contract.Text(value, "directory", 4096), Contract.Text(value, "container", 32),
            value["segmented"]!.GetValue<bool>(), value["segmentSeconds"]!.GetValue<double>());
        result.Validate(); return result;
    }
}
