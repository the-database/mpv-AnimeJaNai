using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons;

// Trusted producer interfaces. Guests see only the versioned RPC contract.
public sealed record FrameRequest(int Width = 64, int Height = 36, int MaxFps = 30,
    string Stage = "processed", bool CharacterMask = false)
{
    public const int MaxBytes = 320 * 180 * 4;
    public const string Model = "skytnt.anime-seg.isnetis.v1";
    internal string Channel => CharacterMask ? "characterMask" : Stage;
    public void Validate() => Contract.Require(Width is >= 1 and <= 320 && Height is >= 1 and <= 180 && MaxFps >= 1 &&
        MaxFps <= (CharacterMask ? 30 : 60) && Stage is "processed" or "source" && (!CharacterMask || Stage == "source"),
        "invalid_request", "Samples require 1..320 by 1..180 pixels, source/processed stage and 1..60 Hz (character masks: source, at most 30 Hz).");
    public JsonObject Describe() => new() { ["stage"] = Stage, ["format"] = "bgra8", ["width"] = Width, ["height"] = Height, ["maxFps"] = MaxFps };
}

public sealed class FramePacket
{
    private readonly JsonObject metadata;
    private readonly byte[] pixels;
    private readonly byte[] mask;
    public FramePacket(JsonObject metadata, ReadOnlySpan<byte> pixels, ReadOnlySpan<byte> mask = default)
    {
        long width = Contract.Number(metadata, "width"), height = Contract.Number(metadata, "height");
        Contract.Require(width is >= 1 and <= 320 && height is >= 1 and <= 180 && pixels.Length == width * height * 4,
            "invalid_frame", "Invalid frame dimensions or payload.");
        Contract.Require(Contract.Text(metadata, "format", 16) == "bgra8" && Contract.Text(metadata, "stage", 32) is "processed" or "source",
            "invalid_frame", "Unsupported sample representation.");
        Contract.Require(mask.IsEmpty || (mask.Length == width * height * 2 && metadata["stage"]?.GetValue<string>() == "source" &&
            metadata["analysis"]?["model"]?.GetValue<string>() == FrameRequest.Model &&
            metadata["analysis"]?["format"]?.GetValue<string>() == "unorm16le"), "invalid_frame", "Invalid matched character mask.");
        this.metadata = (JsonObject)metadata.DeepClone();
        this.pixels = pixels.ToArray();
        this.mask = mask.ToArray();
    }
    public JsonObject Metadata => (JsonObject)metadata.DeepClone();
    public ReadOnlyMemory<byte> Pixels => pixels;
    public ReadOnlyMemory<byte> Mask => mask;
}

public interface IFrameSubscription : IDisposable
{
    // Latest unread sample, or null. This must not wait for GPU/consumer progress.
    FramePacket? ReadLatest();
    JsonObject Status() => new() { ["state"] = "ready" };
}

public interface IFrameProcessingSession : IProcessingSession
{
    IFrameSubscription SubscribeFrames(FrameRequest request);
}

public sealed record BrokerResponse(JsonNode? Result, ReadOnlyMemory<byte> Binary = default);
