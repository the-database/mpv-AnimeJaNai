using System.Text.Json.Nodes;
using AnimeJaNai.Addons.Native;

namespace AnimeJaNai.Addons;

internal sealed record StreamRequest(NativeEncoding Encoding, OutputPlayback Playback, bool Segmented, double SegmentSeconds, string NativeMode = "required")
{
    internal static StreamRequest ParseOutput(JsonObject parameters)
    {
        var destination = parameters["destination"]!.AsObject();
        return Parse(new() { ["encoding"] = parameters["encoding"]?.DeepClone(), ["playback"] = parameters["playback"]?.DeepClone(),
            ["mode"] = destination["mode"]?.DeepClone(), ["segmentSeconds"] = destination["segmentSeconds"]?.DeepClone() });
    }
    internal void Validate()
    {
        Encoding.Validate(); Playback.Validate();
        Contract.Require(NativeMode is "required" or "prepare" or "check", "invalid_stream", "Unknown trusted stream mode.");
        Contract.Require(double.IsFinite(SegmentSeconds) && SegmentSeconds is >= .5 and <= 6, "invalid_stream", "Segment duration must be between 0.5 and 6 seconds.");
    }
    internal static void ValidateSource(JsonObject probe)
    {
        var video = probe["tracks"]!.AsArray().OfType<JsonObject>().Where(t => t["type"]?.GetValue<string>() == "video").ToArray();
        Contract.Require(video.Length > 0, "stream_format_unavailable", "Served output requires a supported video source.");
        Contract.Require(video.All(t => t["fieldOrder"]?.GetValue<string>() != "interlaced" &&
            t["colorTransfer"]?.GetValue<string>() is not ("smpte2084" or "arib-std-b67") &&
            t["hasMasteringDisplayMetadata"]?.GetValue<bool>() != true && t["hasContentLightMetadata"]?.GetValue<bool>() != true),
            "stream_format_unavailable", "HDR and interlaced served output are not qualified. Choose a progressive SDR source.");
    }
    internal static StreamRequest Parse(JsonObject value)
    {
        Contract.Require(value["encoding"] is JsonObject, "invalid_stream", "Choose the stream encoding.");
        Contract.Require(value["playback"] is null or JsonObject, "invalid_stream", "Invalid playback options.");
        string mode = value["mode"] is null ? "segments" : Contract.Text(value, "mode", 16);
        Contract.Require(mode is "segments" or "continuous", "invalid_stream", "Choose segments or continuous output.");
        double seconds = 1;
        if (value["segmentSeconds"] is not null)
            Contract.Require(value["segmentSeconds"] is JsonValue duration && duration.TryGetValue<double>(out seconds), "invalid_stream", "Invalid segment duration.");
        var result = new StreamRequest(NativeEncoding.Parse(value["encoding"]!.AsObject()),
            value["playback"] is JsonObject playback ? OutputPlayback.Parse(playback) : new(), mode == "segments", seconds);
        result.Validate(); return result;
    }
}

internal interface IMediaStreamProvider
{
    bool SupportsStreams { get; }
    bool SupportsReadiness => false;
    Task<IProcessingSession> OpenStreamAsync(ProbeRequest source, string? profile, StreamRequest output, string cacheDirectory, CancellationToken token);
}
internal interface IMediaStreamProducer : IControllableProcessingSession
{
    Task SetDemandAsync(double position, CancellationToken token);
}
