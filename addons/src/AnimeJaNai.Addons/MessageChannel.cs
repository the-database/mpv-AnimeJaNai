using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons;

public sealed class MessageChannel(Stream input, Stream output)
{
    private readonly byte[] buffer = new byte[4096];
    private int position, available;
    public async Task<JsonObject> ReadAsync(CancellationToken cancellationToken)
    {
        using var line = new MemoryStream();
        while (true)
        {
            if (position == available)
            {
                available = await input.ReadAsync(buffer, cancellationToken);
                position = 0;
                if (available == 0) throw new AddonException("worker_exited", "Addon closed its message channel.");
            }
            int newline = Array.IndexOf(buffer, (byte)'\n', position, available - position);
            int end = newline < 0 ? available : newline;
            int length = end - position;
            Contract.Require(line.Length + length <= Contract.MaxMessageBytes, "message_too_large", "Addon message exceeds 128 KiB.");
            line.Write(buffer, position, length);
            position = newline < 0 ? available : newline + 1;
            if (newline >= 0) return Contract.ParseObject(line.GetBuffer().AsSpan(0, (int)line.Length));
        }
    }

    public async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
        => await WriteBinaryAsync(message, ReadOnlyMemory<byte>.Empty, cancellationToken);

    public async Task WriteBinaryAsync(JsonObject message, ReadOnlyMemory<byte> binary, CancellationToken cancellationToken)
    {
        Contract.Require(binary.Length <= FrameRequest.MaxBytes, "message_too_large", "Binary response exceeds its bound.");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message, Contract.Json);
        Contract.Require(bytes.Length <= Contract.MaxMessageBytes, "message_too_large", "Host message exceeds 128 KiB.");
        await output.WriteAsync(bytes, cancellationToken);
        await output.WriteAsync(new byte[] { (byte)'\n' }, cancellationToken);
        if (!binary.IsEmpty) await output.WriteAsync(binary, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    public async Task<byte[]> ReadBinaryAsync(int length, CancellationToken token)
    {
        Contract.Require(length is >= 0 and <= FrameRequest.MaxBytes, "message_too_large", "Invalid binary payload length.");
        byte[] result = new byte[length];
        int buffered = Math.Min(length, available - position);
        buffer.AsSpan(position, buffered).CopyTo(result); position += buffered;
        try { await input.ReadExactlyAsync(result.AsMemory(buffered), token); }
        catch (EndOfStreamException) { throw new AddonException("worker_exited", "Binary response ended before its declared length."); }
        return result;
    }
}
