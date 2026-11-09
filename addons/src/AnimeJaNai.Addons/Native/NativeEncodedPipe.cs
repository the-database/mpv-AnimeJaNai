using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AnimeJaNai.Addons.Native;

// The parent retains only the read end. One write handle is duplicated into
// its trusted, already-supervised media worker before opening the start gate.
// A slow consumer fills the OS pipe and backpressures only that media process.
internal sealed class NativeEncodedPipe : IDisposable
{
    private readonly AnonymousPipeServerStream pipe = new(PipeDirection.In, HandleInheritability.None, 65536);
    public Stream Reader => pipe;
    public long DuplicateTo(Process process)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!DuplicateHandle(new IntPtr(-1), pipe.ClientSafePipeHandle, process.SafeHandle, out var target, 0, false, 2))
            throw new IOException("Could not share the private encoded output pipe.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        pipe.DisposeLocalCopyOfClientHandle();
        return target.ToInt64();
    }
    public void Dispose() => pipe.Dispose();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, SafePipeHandle source, SafeProcessHandle targetProcess,
        out IntPtr target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
}

// The packaged FFmpeg uses the shared Windows UCRT. Transfer the duplicated
// write handle into that CRT's descriptor table; FFmpeg's pipe protocol makes
// its own descriptor reference. Close ours after libmpv has flushed/destroyed.
internal sealed class NativeEncodedWriter : IDisposable
{
    private int descriptor = -1;
    public int Descriptor => descriptor;
    public NativeEncodedWriter(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        Contract.Require(!handle.IsInvalid && !handle.IsClosed, "native_protocol", "An encoded output handle is required.");
        bool retained = false;
        try
        {
            handle.DangerousAddRef(ref retained);
            descriptor = OpenHandle(handle.DangerousGetHandle(), 0x8001); // _O_BINARY | _O_WRONLY
            Contract.Require(descriptor >= 0, "native_output", "Could not open the private encoded output descriptor.");
            handle.SetHandleAsInvalid(); // _close now owns CloseHandle, exactly once.
        }
        finally { if (retained) handle.DangerousRelease(); }
    }
    public void Dispose()
    {
        int value = Interlocked.Exchange(ref descriptor, -1);
        if (value >= 0) _ = CloseDescriptor(value);
    }
    [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "_open_osfhandle")]
    private static extern int OpenHandle(IntPtr handle, int flags);
    [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "_close")]
    private static extern int CloseDescriptor(int descriptor);
}
