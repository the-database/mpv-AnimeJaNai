using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons.Native;

// Trusted adapter options. Public output negotiation and destination consent
// remain separate; an addon cannot supply FFmpeg options, a path or a handle.
internal sealed record NativeEncoding(string VideoCodec, string Container, int VideoKbps,
    string AudioCodec = "none", int AudioKbps = 128, int KeyframeFrames = 60, double LengthSeconds = 0)
{
    public void Validate()
    {
        Contract.Require(VideoCodec is "h264" or "hevc" or "av1", "invalid_encoding", "Unsupported video codec.");
        Contract.Require(Container is "matroska" or "mpegts" or "fragmentedMp4", "invalid_encoding", "Unsupported output container.");
        Contract.Require(AudioCodec is "none" or "aac" or "opus", "invalid_encoding", "Unsupported audio codec.");
        Contract.Require(Container != "mpegts" || (VideoCodec != "av1" && AudioCodec != "opus"),
            "invalid_encoding", "This MPEG-TS adapter supports H.264/HEVC with optional AAC.");
        Contract.Require(VideoKbps is >= 256 and <= 50000 && AudioKbps is >= 32 and <= 512 && KeyframeFrames is >= 1 and <= 600,
            "invalid_encoding", "Encoding rate or keyframe interval is outside the supported range.");
        Contract.Require(double.IsFinite(LengthSeconds) && LengthSeconds is >= 0 and <= 86400,
            "invalid_encoding", "Invalid encoding duration.");
    }
    public JsonObject ToJson() => new() { ["videoCodec"] = VideoCodec, ["container"] = Container,
        ["videoKbps"] = (long)VideoKbps, ["audioCodec"] = AudioCodec, ["audioKbps"] = (long)AudioKbps,
        ["keyframeFrames"] = (long)KeyframeFrames, ["lengthSeconds"] = LengthSeconds };
    public static NativeEncoding Parse(JsonObject value)
    {
        Contract.Require(value["lengthSeconds"] is JsonValue duration && duration.TryGetValue<double>(out _),
            "invalid_encoding", "Invalid encoding duration.");
        var result = new NativeEncoding(Contract.Text(value, "videoCodec", 16), Contract.Text(value, "container", 32),
            checked((int)Contract.Number(value, "videoKbps")), Contract.Text(value, "audioCodec", 16),
            checked((int)Contract.Number(value, "audioKbps")), checked((int)Contract.Number(value, "keyframeFrames")),
            value["lengthSeconds"]!.GetValue<double>());
        result.Validate(); return result;
    }
}
