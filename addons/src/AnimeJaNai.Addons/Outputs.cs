using System.Text.Json.Nodes;
using AnimeJaNai.Addons.Native;

namespace AnimeJaNai.Addons;

// Public control contract; implementations remain trusted host components.
// A destination is an approved opaque selection, never an arbitrary URL/path.
public sealed record OutputRequest(string DestinationId, string Path, string Method, bool UseCredential,
    string VideoCodec, string Container, int VideoKbps, string AudioCodec = "none", int AudioKbps = 128,
    int KeyframeFrames = 60, double LengthSeconds = 0, OutputPlayback? Playback = null, int? AudioChannels = null)
{
    internal NativeEncoding NativeOptions => new(VideoCodec, Container, VideoKbps, AudioCodec, AudioKbps, KeyframeFrames, LengthSeconds, AudioChannels);
    public void Validate()
    {
        NativeOptions.Validate();
        Playback?.Validate();
        Contract.Require(DestinationId.Length is > 0 and <= 64 && Path.Length is > 0 and <= 2048 && Method is "POST" or "PUT",
            "invalid_output", "Choose an approved service, a relative path and POST or PUT.");
    }
    public static OutputRequest Parse(JsonObject value)
    {
        Contract.Require(value["destination"] is JsonObject && value["encoding"] is JsonObject, "invalid_output", "Choose output encoding and a destination.");
        var destination = (JsonObject)value["destination"]!;
        Contract.Require(Contract.Text(destination, "type", 32) == "httpUpload", "feature_unavailable", "This provider supports approved HTTP upload destinations.");
        Contract.Require(destination["useCredential"] is JsonValue credential && credential.TryGetValue<bool>(out _), "invalid_output", "Choose whether to use a saved credential.");
        var encoding = NativeEncoding.Parse((JsonObject)value["encoding"]!);
        var result = new OutputRequest(Contract.Text(destination, "destinationId", 64), Contract.Text(destination, "path", 2048),
            Contract.Text(destination, "method", 16), destination["useCredential"]!.GetValue<bool>(), encoding.VideoCodec, encoding.Container,
            encoding.VideoKbps, encoding.AudioCodec, encoding.AudioKbps, encoding.KeyframeFrames, encoding.LengthSeconds,
            value["playback"] is null ? null : OutputPlayback.Parse(value["playback"]!.AsObject()), encoding.AudioChannels);
        result.Validate(); return result;
    }
}

// Only trusted producers implement this interface. No Stream object or native
// descriptor is serialized into the guest SDK or management protocol.
public interface IEncodedProcessingSession : IControllableProcessingSession
{
    Stream EncodedOutput { get; }
}
