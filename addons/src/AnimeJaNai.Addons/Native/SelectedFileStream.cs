using System.Runtime.InteropServices;

namespace AnimeJaNai.Addons.Native;

// Private libmpv adapter. A stream callback exposes only this opened file, with
// no path parsing or alternate-file lookup in the callbacks. Keep delegates
// alive until mpv_terminate_destroy has returned.
internal sealed class SelectedFileStream : IDisposable
{
    public const string Protocol = "ajnselected";
    public const string Uri = Protocol + "://media";
    private readonly FileStream file;
    private readonly object gate = new();
    private readonly byte[] buffer = new byte[65536];
    private bool open;
    private volatile bool cancelled;
    private readonly OpenCallback openCallback;
    private readonly ReadCallback read;
    private readonly SeekCallback seek;
    private readonly SizeCallback size;
    private readonly CloseCallback close, cancel;

    public SelectedFileStream(string path)
    {
        SafeFiles.CheckParents(path);
        file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.RandomAccess);
        openCallback = Open; read = Read; seek = Seek; size = Size; close = Close; cancel = _ => cancelled = true;
    }

    public int Register(IntPtr library, IntPtr player) => Marshal.GetDelegateForFunctionPointer<RegisterCallback>(
        NativeLibrary.GetExport(library, "mpv_stream_cb_add_ro"))(player, Protocol, IntPtr.Zero, openCallback);

    private int Open(IntPtr user, IntPtr uri, IntPtr info)
    {
        try
        {
            lock (gate)
            {
                if (open || Marshal.PtrToStringUTF8(uri) != Uri) return -13; // MPV_ERROR_LOADING_FAILED
                file.Position = 0; cancelled = false;
                Marshal.StructureToPtr(new StreamInfo
                {
                    Read = Marshal.GetFunctionPointerForDelegate(read), Seek = Marshal.GetFunctionPointerForDelegate(seek),
                    Size = Marshal.GetFunctionPointerForDelegate(size), Close = Marshal.GetFunctionPointerForDelegate(close),
                    Cancel = Marshal.GetFunctionPointerForDelegate(cancel),
                }, info, false);
                open = true; return 0;
            }
        }
        catch { return -13; } // Managed exceptions must not cross native callbacks.
    }
    private long Read(IntPtr cookie, IntPtr target, ulong count)
    {
        try
        {
            lock (gate)
            {
                if (!open || cancelled) return -1;
                int length = file.Read(buffer, 0, (int)Math.Min(count, (ulong)buffer.Length));
                Marshal.Copy(buffer, 0, target, length); return length;
            }
        }
        catch { return -1; }
    }
    private long Seek(IntPtr cookie, long position)
    {
        try { lock (gate) return !open || cancelled || position < 0 ? -20 : file.Seek(position, SeekOrigin.Begin); }
        catch { return -20; } // MPV_ERROR_GENERIC
    }
    private long Size(IntPtr cookie)
    {
        try { lock (gate) return !open || cancelled ? -20 : file.Length; }
        catch { return -20; }
    }
    private void Close(IntPtr cookie) { lock (gate) open = false; }
    public void Dispose() => file.Dispose();

    [StructLayout(LayoutKind.Sequential)] private struct StreamInfo { public IntPtr Cookie, Read, Seek, Size, Close, Cancel; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int OpenCallback(IntPtr user, IntPtr uri, IntPtr info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long ReadCallback(IntPtr cookie, IntPtr target, ulong count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long SeekCallback(IntPtr cookie, long position);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long SizeCallback(IntPtr cookie);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CloseCallback(IntPtr cookie);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RegisterCallback(IntPtr player,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string protocol, IntPtr user, OpenCallback callback);
}
