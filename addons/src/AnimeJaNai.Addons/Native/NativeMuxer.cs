using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons.Native;

// Runs only inside the supervised media worker. FFmpeg remuxes encoded packets;
// all writes go through these quota-enforcing callbacks, never through Wasm.
internal sealed class NativeMuxer : IDisposable
{
    internal const long MaximumBytes = 2L << 30;
    private readonly string directory;
    private readonly Stream source;
    private readonly CancellationToken token;
    private readonly long maximumBytes;
    private readonly TimeSpan pressureTimeout;
    private readonly Dictionary<long, FileStream> files = [];
    private readonly byte[] writeBuffer = new byte[32768];
    private readonly NativeReadBuffer reader;
    private long nextHandle, accountedBytes, lastMeasured;
    private string? failure;
    private string? runtimeRoot;
    internal MuxDemandGate? Demand { get; init; }
    internal NativeMuxer(string directory, Stream source, CancellationToken token, long maximumBytes = MaximumBytes, int pressureTimeoutMs = 15000, NativeReadMetrics? readMetrics = null)
    {
        Contract.Require(maximumBytes is > 0 and <= MaximumBytes, "invalid_stream_cache", "Invalid native cache quota.");
        this.maximumBytes = maximumBytes;
        Contract.Require(pressureTimeoutMs is >= 1 and <= 15000, "invalid_stream_cache", "Invalid storage pressure deadline.");
        pressureTimeout = TimeSpan.FromMilliseconds(pressureTimeoutMs);
        this.directory = Path.GetFullPath(directory); this.source = source; this.token = token;
        reader = new NativeReadBuffer(source, token, metrics: readMetrics);
        SafeFiles.CheckParents(this.directory);
        Contract.Require(Directory.Exists(this.directory) && Directory.EnumerateFileSystemEntries(this.directory).All(p => Path.GetFileName(p) == ".owner"),
            "invalid_stream_cache", "Native muxing requires an empty owned cache directory.");
    }
    internal static bool AllowedFile(string name) => name is "index.csv" or "index.csv.tmp" or "index.m3u8" or "index.m3u8.tmp" or
        "init.mp4" or "init.mp4.tmp" or "continuous.mkv" or "continuous.mp4" or "continuous.ts" or "continuous.json" ||
        Regex.IsMatch(name, @"\Apart_[0-9]{8}\.(?:mkv|m4s|ts|json)(?:\.tmp)?\z", RegexOptions.CultureInvariant);
    internal long Open(IntPtr _, string url)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            string full = Path.GetFullPath(url, directory), name = Path.GetFileName(full);
            Contract.Require(Path.GetDirectoryName(full)!.Equals(directory, StringComparison.OrdinalIgnoreCase) && AllowedFile(name) && files.Count < 16,
                "invalid_stream_resource", "Native muxer requested an unexpected output object.");
            SafeFiles.CheckParents(full);
            Contract.Require(!files.Values.Any(f => f.Name.Equals(full, StringComparison.OrdinalIgnoreCase)), "native_mux_failed", "Output object already has a writer.");
            Measure();
            long old = File.Exists(full) ? new FileInfo(full).Length : 0;
            var file = new FileStream(full, FileMode.Create, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 32768);
            accountedBytes -= old;
            long handle = ++nextHandle; files.Add(handle, file); return handle;
        }
        catch (Exception error) { Fail(error); return -1; }
    }
    private void Measure()
    {
        foreach (var writer in files.Values) writer.Flush();
        long total = 0; int count = 0;
        foreach (string path in Directory.EnumerateFiles(directory))
        {
            if (Path.GetFileName(path) == ".owner") continue;
            Contract.Require(++count <= 512 && AllowedFile(Path.GetFileName(path)), "buffer_limit_reached", "Stream cache object limit reached.");
            SafeFiles.CheckParents(path);
            try { total = checked(total + new FileInfo(path).Length); }
            catch (FileNotFoundException) { } // Host expired an eligible closed object.
        }
        // Deleted open files still consume disk until their handles close.
        foreach (var file in files.Values)
            if (!File.Exists(file.Name)) total = checked(total + file.Length);
        accountedBytes = total; lastMeasured = Stopwatch.GetTimestamp();
        Contract.Require(total <= maximumBytes, "buffer_limit_reached", "Stream cache exceeds its storage limit.");
    }
    internal int Read(IntPtr _, IntPtr target, int count)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (count <= 0) return -22;
            return reader.Read(target, count);
        }
        catch (Exception error) { Fail(error); return -5; }
    }
    internal int Write(IntPtr _, long handle, IntPtr bytes, int count)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            Contract.Require(failure is null && files.ContainsKey(handle), "native_mux_failed", "Output writer is unavailable.");
            Contract.Require(count is >= 0 and <= 8 * 1024 * 1024, "buffer_limit_reached", "Native write exceeds its transfer bound.");
            var file = files[handle];
            if (Stopwatch.GetElapsedTime(lastMeasured) >= TimeSpan.FromMilliseconds(250))
            {
                Measure();
            }
            long growth = Math.Max(0, checked(file.Position + count) - file.Length);
            if (accountedBytes + growth > maximumBytes)
            {
                long waiting = Stopwatch.GetTimestamp(); Demand?.StoragePause(true);
                try
                {
                    while (accountedBytes + growth > maximumBytes)
                    {
                        token.ThrowIfCancellationRequested();
                        Contract.Require(Stopwatch.GetElapsedTime(waiting) < pressureTimeout, "buffer_limit_reached", "Stream cache reached its limit before required objects could expire.");
                        if (token.WaitHandle.WaitOne(25)) token.ThrowIfCancellationRequested();
                        Measure();
                    }
                }
                finally { Demand?.StoragePause(false); }
            }
            accountedBytes += growth;
            for (int offset = 0; offset < count;)
            {
                int length = Math.Min(writeBuffer.Length, count - offset);
                Marshal.Copy(IntPtr.Add(bytes, offset), writeBuffer, 0, length);
                file.Write(writeBuffer, 0, length); offset += length;
            }
            return count;
        }
        catch (Exception error) { Fail(error); return failure == "buffer_limit_reached" ? -28 : -5; }
    }
    private long Seek(IntPtr _, long handle, long offset, int whence)
    {
        try
        {
            token.ThrowIfCancellationRequested(); var file = files[handle];
            if ((whence & 0x10000) != 0) return file.Length;
            whence &= ~0x20000;
            long target = whence switch { 0 => offset, 1 => checked(file.Position + offset), 2 => checked(file.Length + offset), _ => -1 };
            Contract.Require(target >= 0 && target <= maximumBytes, "buffer_limit_reached", "Output seek exceeds cache bounds.");
            file.Position = target; return target;
        }
        catch (Exception error) { Fail(error); return -5; }
    }
    internal int Close(IntPtr _, long handle)
    {
        try
        {
            if (files.Remove(handle, out var file))
            {
                string path = file.Name; file.Dispose();
                if (runtimeRoot is not null && failure is null && !token.IsCancellationRequested &&
                    (Regex.IsMatch(Path.GetFileName(path), @"\Apart_[0-9]{8}\.(?:mkv|m4s|ts)(?:\.tmp)?\z") || Path.GetFileName(path) is "continuous.mkv" or "continuous.mp4" or "continuous.ts"))
                    InspectCompleted(path);
            }
            return 0;
        }
        catch (Exception error) { Fail(error); return -5; }
    }
    private void InspectCompleted(string path)
    {
        string mediaName = Path.GetFileName(path).Replace(".tmp", "", StringComparison.Ordinal);
        bool fragment = mediaName.EndsWith(".m4s", StringComparison.Ordinal);
        using Stream input = fragment ? new JoinedMediaStream(Path.Combine(directory, "init.mp4"), path) :
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var metadata = NativeProbeWorker.Probe(runtimeRoot!, input, new string('0', 64), deadline.Token);
        metadata.Remove("representationId"); metadata.Remove("chapters"); metadata.Remove("attachments");
        foreach (var track in metadata["tracks"]!.AsArray().OfType<JsonObject>())
        { track.Remove("trackId"); track.Remove("title"); track.Remove("language"); }
        metadata["resourceFile"] = mediaName;
        byte[] bytes = Encoding.UTF8.GetBytes(metadata.ToJsonString());
        Contract.Require(bytes.Length <= 32768, "native_protocol", "Encoded media metadata exceeds its bound.");
        string name = mediaName.StartsWith("continuous.", StringComparison.Ordinal) ? "continuous.json" : Path.GetFileNameWithoutExtension(mediaName) + ".json";
        long writer = Open(IntPtr.Zero, name); Contract.Require(writer > 0, failure ?? "native_mux_failed", "Cannot write encoded metadata.");
        IntPtr memory = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            Contract.Require(Write(IntPtr.Zero, writer, memory, bytes.Length) == bytes.Length, failure ?? "native_mux_failed", "Cannot complete encoded metadata.");
        }
        finally { Marshal.FreeHGlobal(memory); Close(IntPtr.Zero, writer); }
    }
    private void Fail(Exception error) => failure ??= error is AddonException addon ? addon.Code : error is OperationCanceledException ? "stream_cancelled" : "native_mux_failed";
    internal void Run(string root, string container, bool segmented, double seconds)
    {
        runtimeRoot = root;
        IntPtr library = NativeRuntime.Load(root);
        Contract.Require(NativeLibrary.TryGetExport(library, "mpv_ajn_mux_v1", out var address), "feature_unavailable", "Matching native muxer runtime is unavailable.");
        var function = Marshal.GetDelegateForFunctionPointer<MuxFunction>(address);
        ReadFunction read = Read; CancelFunction cancel = _ => token.IsCancellationRequested || failure is not null ? 1 : 0;
        PacketFunction packet = (_, start, end) =>
        {
            try { Demand?.Admit(start, end, token); return 0; }
            catch (Exception error) { Fail(error); return -5; }
        };
        OpenFunction open = Open; WriteFunction write = Write; SeekFunction seek = Seek; CloseFunction close = Close;
        try
        {
            int result = function(new IntPtr(1), read, cancel, packet, open, write, seek, close, container, segmented ? 1 : 0, seconds);
            token.ThrowIfCancellationRequested();
            Contract.Require(result >= 0 && failure is null, failure ?? "native_mux_failed", "The native media muxer could not complete its output.");
        }
        finally { GC.KeepAlive(read); GC.KeepAlive(cancel); GC.KeepAlive(packet); GC.KeepAlive(open); GC.KeepAlive(write); GC.KeepAlive(seek); GC.KeepAlive(close); }
    }
    public void Dispose() { foreach (var file in files.Values) file.Dispose(); files.Clear(); }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReadFunction(IntPtr opaque, IntPtr data, int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CancelFunction(IntPtr opaque);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PacketFunction(IntPtr opaque, double start, double end);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long OpenFunction(IntPtr opaque, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int WriteFunction(IntPtr opaque, long handle, IntPtr data, int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long SeekFunction(IntPtr opaque, long handle, long offset, int whence);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CloseFunction(IntPtr opaque, long handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int MuxFunction(IntPtr opaque, ReadFunction read, CancelFunction cancel, PacketFunction packet,
        OpenFunction open, WriteFunction write, SeekFunction seek, CloseFunction close,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string container, int segmented, double seconds);
}
