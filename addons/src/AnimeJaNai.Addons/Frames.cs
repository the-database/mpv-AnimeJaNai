using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons;

// Trusted producer interfaces. Guests see only the versioned RPC contract.
public sealed record FrameRequest(int Width = 64, int Height = 36, int MaxFps = 30)
{
    public const int MaxBytes = 320 * 180 * 4;
    public void Validate() => Contract.Require(Width is >= 1 and <= 320 && Height is >= 1 and <= 180 && MaxFps is >= 1 and <= 60,
        "invalid_request", "Sample dimensions must be 1..320 by 1..180 and rate 1..60 Hz.");
    public JsonObject Describe() => new() { ["stage"] = "processed", ["format"] = "bgra8", ["width"] = Width, ["height"] = Height, ["maxFps"] = MaxFps };
}

public sealed class FramePacket
{
    private readonly JsonObject metadata;
    private readonly byte[] pixels;
    public FramePacket(JsonObject metadata, ReadOnlySpan<byte> pixels)
    {
        long width = Contract.Number(metadata, "width"), height = Contract.Number(metadata, "height");
        Contract.Require(width is >= 1 and <= 320 && height is >= 1 and <= 180 && pixels.Length == width * height * 4,
            "invalid_frame", "Invalid frame dimensions or payload.");
        Contract.Require(Contract.Text(metadata, "format", 16) == "bgra8" && Contract.Text(metadata, "stage", 32) == "processed",
            "invalid_frame", "Unsupported sample representation.");
        this.metadata = (JsonObject)metadata.DeepClone();
        this.pixels = pixels.ToArray();
    }
    public JsonObject Metadata => (JsonObject)metadata.DeepClone();
    public ReadOnlyMemory<byte> Pixels => pixels;
}

public interface IFrameSubscription : IDisposable
{
    // Latest unread sample, or null. This must not wait for GPU/consumer progress.
    FramePacket? ReadLatest();
}

public interface IFrameProcessingSession : IProcessingSession
{
    IFrameSubscription SubscribeFrames(FrameRequest request);
}

public sealed record BrokerResponse(JsonNode? Result, ReadOnlyMemory<byte> Binary = default);
