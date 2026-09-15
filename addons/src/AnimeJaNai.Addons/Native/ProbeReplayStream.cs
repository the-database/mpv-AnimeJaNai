namespace AnimeJaNai.Addons.Native;

// A bounded prefix for probing a forward-only source. Playback reuses those
// exact bytes, then continues the same connection. This does not promise seek.
internal sealed class ProbeReplayStream(Stream source) : Stream
{
    private MemoryStream? prefix = new();
    private bool replay;
    private long position;
    internal void BeginReplay() { replay = true; position = 0; prefix!.Position = 0; }
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (replay && prefix is not null)
        {
            int copied = prefix.Read(buffer, offset, count);
            if (copied > 0) { position += copied; return copied; }
            prefix.Dispose(); prefix = null;
        }
        if (!replay) Contract.Require(prefix!.Length + count <= 64 * 1024 * 1024, "probe_limit", "Source probing exceeded its prefix budget.");
        int read = source.Read(buffer, offset, count);
        if (!replay) prefix!.Write(buffer, offset, read);
        position += read; return read;
    }
    public override bool CanRead => source.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => source.Length;
    public override long Position { get => position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) { prefix?.Dispose(); prefix = null; source.Dispose(); } base.Dispose(disposing); }
}
