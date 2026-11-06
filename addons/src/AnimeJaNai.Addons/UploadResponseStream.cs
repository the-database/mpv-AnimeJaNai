namespace AnimeJaNai.Addons;

// HTTP/1.1 HttpClient waits for request content before returning a response.
// This read-only observer lets an early final response cancel our live content
// first. HttpClient still owns parsing, TLS validation and response acceptance.
// No header values/body are exposed, modified, or used to grant any authority.
internal sealed class UploadResponseStream(Stream inner, Action<int?> finalResponse) : Stream
{
    private readonly byte[] header = new byte[16 * 1024];
    private int count, total;
    private bool done;
    private void Observe(ReadOnlySpan<byte> bytes)
    {
        if (done) return;
        if (bytes.IsEmpty) { done = true; finalResponse(null); return; }
        foreach (byte value in bytes)
        {
            if (++total > header.Length) { done = true; finalResponse(null); return; }
            header[count++] = value;
            if (count < 4 || !header.AsSpan(count - 4, 4).SequenceEqual("\r\n\r\n"u8)) continue;
            var block = header.AsSpan(0, count);
            int end = block.IndexOf("\r\n"u8);
            bool valid = end >= 12 && (block.StartsWith("HTTP/1.1 "u8) || block.StartsWith("HTTP/1.0 "u8)) &&
                block[9] is >= (byte)'1' and <= (byte)'5' && block[10] is >= (byte)'0' and <= (byte)'9' &&
                block[11] is >= (byte)'0' and <= (byte)'9' && (end == 12 || block[12] == ' ');
            int? status = valid ? (block[9] - '0') * 100 + (block[10] - '0') * 10 + block[11] - '0' : null;
            if (status is >= 100 and < 200 && status != 101) { count = 0; continue; }
            done = true; finalResponse(status); return;
        }
    }
    public override int Read(byte[] buffer, int offset, int count)
    { int n = inner.Read(buffer, offset, count); if (count > 0) Observe(buffer.AsSpan(offset, n)); return n; }
    public override int Read(Span<byte> buffer)
    { int n = inner.Read(buffer); if (!buffer.IsEmpty) Observe(buffer[..n]); return n; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    { int n = await inner.ReadAsync(buffer, token); if (!buffer.IsEmpty) Observe(buffer.Span[..n]); return n; }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
        ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => inner.WriteAsync(buffer, token);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) => inner.WriteAsync(buffer, offset, count, token);
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);
    public override bool CanRead => inner.CanRead;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
}
