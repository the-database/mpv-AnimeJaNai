using System.Runtime.InteropServices;

namespace AnimeJaNai.Addons.Native;

// AVIO read requests are not bounded by the AVIO scratch-buffer size. In
// particular, Matroska attachments and large packets can request many MiB.
// A callback may return a short read; never allocate or loop to fill a request.
internal sealed class NativeReadBuffer(Stream source, CancellationToken token,
    long maximumBytes = long.MaxValue, string budgetError = "native_read_limit",
    NativeReadMetrics? metrics = null)
{
    internal const int Capacity = 32768;
    internal const int EndOfFile = -541478725;
    private readonly byte[] buffer = new byte[Capacity];
    internal long BytesRead { get; private set; }

    // Exceptions are translated by each owning callback, preserving its error
    // codes, cancellation ownership and cleanup. No exception crosses the ABI.
    internal int Read(IntPtr destination, int requested)
    {
        token.ThrowIfCancellationRequested();
        Contract.Require(requested > 0, "native_read_invalid", "Native read length must be positive.");
        long remaining = maximumBytes - BytesRead;
        Contract.Require(remaining > 0, budgetError, "Native input exceeded its read budget.");
        int wanted = (int)Math.Min(Math.Min((long)requested, buffer.Length), remaining);
        int count = source.Read(buffer, 0, wanted);
        if ((uint)count > (uint)wanted) throw new IOException("Source returned an invalid read count.");
        BytesRead += count;
        metrics?.Record(requested, count);
        if (count == 0) return EndOfFile;
        Marshal.Copy(buffer, 0, destination, count);
        return count;
    }
}

// Internal, bounded diagnostics used to qualify actual libavformat callers.
// These counters contain no source names, URLs, headers or media contents.
internal sealed class NativeReadMetrics
{
    internal int LargestRequest { get; private set; }
    internal long LargeRequests { get; private set; }
    internal long BytesRead { get; private set; }
    internal void Record(int requested, int count)
    {
        LargestRequest = Math.Max(LargestRequest, requested);
        if (requested > NativeReadBuffer.Capacity) LargeRequests++;
        BytesRead += count;
    }
}
