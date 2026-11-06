using System.Runtime.InteropServices;

namespace AnimeJaNai.Addons.Native;

internal static class NativeSubtitles
{
    internal const int MaximumBytes = 16 * 1024 * 1024;
    internal static byte[] Extract(string root, Stream source, int streamIndex, SubtitleRequest options, CancellationToken token, NativeReadMetrics? metrics = null)
    {
        IntPtr library = NativeRuntime.Load(root);
        Contract.Require(NativeLibrary.TryGetExport(library, "mpv_ajn_subtitles_v1", out var address), "feature_unavailable", "The native subtitle runtime is unavailable.");
        var extract = Marshal.GetDelegateForFunctionPointer<ExtractFunction>(address);
        using var output = new MemoryStream(); byte[] buffer = new byte[32768]; string? failure = null;
        var reader = new NativeReadBuffer(source, token, 64L << 30, "subtitle_read_limit", metrics);
        ReadFunction read = (_, destination, count) =>
        {
            try
            {
                // Preserve this callback's existing invalid-request error code.
                token.ThrowIfCancellationRequested();
                Contract.Require(count > 0, "subtitle_read_limit", "Invalid subtitle read request.");
                return reader.Read(destination, count);
            }
            catch (Exception error) { failure ??= error is AddonException addon ? addon.Code : "subtitle_read_failed"; return -5; }
        };
        SeekFunction seek = (_, offset, whence) =>
        {
            try
            {
                if (token.IsCancellationRequested) return -5;
                if ((whence & 0x10000) != 0) return source.Length;
                if (!source.CanSeek) return -38;
                whence &= ~0x20000; return whence is >= 0 and <= 2 ? source.Seek(offset, (SeekOrigin)whence) : -22;
            }
            catch { return -5; }
        };
        WriteFunction write = (_, data, count) =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                Contract.Require(count is >= 0 and <= 65536 && output.Length + count <= MaximumBytes, "subtitle_result_too_large", "Extracted subtitles exceed 16 MiB.");
                for (int offset = 0; offset < count;)
                { int length = Math.Min(buffer.Length, count - offset); Marshal.Copy(IntPtr.Add(data, offset), buffer, 0, length); output.Write(buffer, 0, length); offset += length; }
                return count;
            }
            catch (Exception error) { failure ??= error is AddonException addon ? addon.Code : "subtitle_write_failed"; return -5; }
        };
        CancelFunction cancel = _ => token.IsCancellationRequested || failure is not null ? 1 : 0;
        try
        {
            int result = extract(new IntPtr(1), read, seek, cancel, write, source.CanSeek ? 1 : 0, streamIndex, options.StartSeconds, options.EndSeconds);
            token.ThrowIfCancellationRequested();
            Contract.Require(result >= 0, failure ?? (result == -38 ? "subtitle_format_unavailable" : "subtitle_extract_failed"), "The selected subtitles could not be extracted.");
            return output.ToArray();
        }
        finally { GC.KeepAlive(read); GC.KeepAlive(seek); GC.KeepAlive(write); GC.KeepAlive(cancel); }
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReadFunction(IntPtr opaque, IntPtr target, int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long SeekFunction(IntPtr opaque, long offset, int whence);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int WriteFunction(IntPtr opaque, IntPtr source, int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CancelFunction(IntPtr opaque);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ExtractFunction(IntPtr opaque, ReadFunction read, SeekFunction seek, CancelFunction cancel, WriteFunction write, int seekable, int index, double start, double end);
}
