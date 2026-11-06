namespace AnimeJaNai.Addons.Native;

// Logical init+fragment input for inspecting one completed fragmented MP4 object.
// Opens only two host-owned files; no temporary concatenated media is created.
internal sealed class JoinedMediaStream : Stream
{
    private readonly FileStream first, second;
    private readonly long boundary, length;
    private long position;
    internal JoinedMediaStream(string initialization, string fragment)
    {
        SafeFiles.CheckParents(initialization); SafeFiles.CheckParents(fragment);
        first = new(initialization, FileMode.Open, FileAccess.Read, FileShare.Read);
        try { second = new(fragment, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch { first.Dispose(); throw; }
        boundary = first.Length; length = checked(boundary + second.Length);
    }
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (position >= length || count == 0) return 0;
        var source = position < boundary ? first : second;
        source.Position = position < boundary ? position : position - boundary;
        int size = (int)Math.Min(count, (position < boundary ? boundary : length) - position);
        int read = source.Read(buffer, offset, size); position += read; return read;
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        long next = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => checked(position + offset), SeekOrigin.End => checked(length + offset), _ => -1 };
        if (next < 0 || next > length) throw new IOException("Invalid selected media seek.");
        return position = next;
    }
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => position; set => Seek(value, SeekOrigin.Begin); }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) { first.Dispose(); second.Dispose(); } base.Dispose(disposing); }
}
