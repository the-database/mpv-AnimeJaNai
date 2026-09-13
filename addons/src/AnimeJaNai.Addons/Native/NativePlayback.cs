using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons.Native;

// Trusted implementation detail, used only inside a supervised media process.
// No libmpv handle, raw command, option string or file path enters the guest API.
internal sealed class NativePlayback : IDisposable
{
    private readonly IntPtr library;
    private readonly SelectedFileStream selectedFile;
    private IntPtr player;
    private readonly Destroy destroy;
    private readonly Command command;
    private readonly WaitEvent wait;
    private readonly ErrorText errorText;
    private readonly JsonObject status = new() { ["state"] = "opening", ["paused"] = false };
    private long nextCommand;
    private readonly object controlGate = new();
    public bool Ended { get; private set; }
    public bool Failed { get; private set; }

    public NativePlayback(string installRoot, string source, string configuration, string workDirectory, int slot, string backend, long sampleMapping = 0)
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8) throw new PlatformNotSupportedException("Native media currently requires Windows x64.");
        Contract.Require(Path.IsPathFullyQualified(source) && File.Exists(source), "invalid_source", "A selected local media file is required.");
        Contract.Require(slot is >= 1 and <= 9 or >= 1001 and <= 1003 or >= 1010 and <= 1013, "invalid_profile", "Unsupported native profile.");
        Contract.Require(backend is "DirectML" or "TensorRT", "invalid_profile", "Unsupported native backend.");
        installRoot = Path.GetFullPath(installRoot);
        library = NativeLibrary.Load(Path.Combine(installRoot, "libmpv-2.dll"), typeof(NativePlayback).Assembly,
            DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
        T Load<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
        try
        {
            uint version = Load<ApiVersion>("mpv_client_api_version")();
            Contract.Require(version >> 16 == 2 && (version & 65535) >= 5, "native_version", "Native client API 2.5 or later within major 2 is required.");
            var create = Load<Create>("mpv_create");
            destroy = Load<Destroy>("mpv_terminate_destroy");
            command = Load<Command>("mpv_command_async");
            wait = Load<WaitEvent>("mpv_wait_event");
            errorText = Load<ErrorText>("mpv_error_string");
            var option = Load<Option>("mpv_set_option_string");
            var initialize = Load<Initialize>("mpv_initialize");
            var observe = Load<Observe>("mpv_observe_property");
            player = create();
            Contract.Require(player != IntPtr.Zero, "native_start", "Could not create the native processing context.");
            void Set(string key, string value) => Check(option(player, key, value));
            foreach (string name in new[] { "config", "load-scripts", "osc", "ytdl", "load-stats-overlay", "load-console",
                "load-auto-profiles", "load-select", "load-positioning", "load-commands", "load-context-menu",
                "input-default-bindings", "input-terminal", "terminal", "audio", "sub", "access-references" }) Set(name, "no");
            Set("sub-auto", "no"); Set("audio-file-auto", "no"); Set("cover-art-auto", "no");
            Set("vo", "null"); Set("hwdec", backend == "DirectML" ? "d3d11va" : "cuda"); Set("idle", "yes");
            // Use our already-opened file, restrict container parsers, and deny
            // nested protocol opens. Playlists and external references cannot
            // turn a selected file into additional file/network access.
            Set("demuxer", "lavf");
            const string formats = "matroska,webm,mov,avi,mpegts";
            Set("demuxer-lavf-o", "protocol_whitelist=ajnselected,format_whitelist=%" + formats.Length + "%" + formats);
            string inference = Path.Combine(installRoot, "animejanai", "inference");
            var parameters = new Dictionary<string, string>
            {
                ["lib"] = Path.Combine(inference, "aji.dll"), ["conf"] = configuration,
                ["model-dir"] = Path.Combine(installRoot, "animejanai", "onnx"),
                ["rife-model-dir"] = Path.Combine(installRoot, "animejanai", "rife"),
                ["trtexec"] = Path.Combine(inference, "trtexec.exe"), ["stats"] = Path.Combine(workDirectory, "inference.log"),
            };
            string filters = "@aji:animejanai:" + string.Join(':', parameters.Select(p => p.Key + "=" + Quote(p.Value))) + ":slot=" + slot;
            if (sampleMapping > 0) filters += ",@ajn-sample:ajn-sample:mapping=" + sampleMapping.ToString(CultureInfo.InvariantCulture);
            Set("vf", filters);
            selectedFile = new SelectedFileStream(source);
            Check(selectedFile.Register(library, player));
            Check(initialize(player));
            ulong number = 0;
            foreach (var property in Properties)
                Check(observe(player, ++number, property.Native, property.Format));
            Send("loadfile", SelectedFileStream.Uri, "replace");
        }
        catch
        {
            if (player != IntPtr.Zero) destroy!(player);
            selectedFile?.Dispose();
            NativeLibrary.Free(library); throw;
        }
    }

    public JsonObject Poll()
    {
        for (int i = 0; i < 256; i++)
        {
            var message = Marshal.PtrToStructure<Event>(wait(player, i == 0 ? .05 : 0));
            if (message.Id == 0) break;
            switch (message.Id)
            {
                case 1: Ended = true; break;
                case 5:
                    status["lastCommandId"] = message.UserData;
                    status["lastCommandError"] = message.Error < 0 ? Describe(message.Error) : null;
                    break;
                case 7:
                    int reason = Marshal.ReadInt32(message.Data);
                    int error = Marshal.ReadInt32(message.Data, 4);
                    Ended = true; Failed = reason is 4 or 5 || error < 0;
                    status["state"] = Failed ? "failed" : "completed";
                    if (Failed) status["error"] = reason == 5 ? "Playlist redirection is not a selected media source." : Describe(error);
                    break;
                case 8: status["state"] = "loading"; break;
                case 21: status["state"] = status["paused"]?.GetValue<bool>() == true ? "paused" : "running"; break;
                case 22:
                    var property = Marshal.PtrToStructure<Property>(message.Data);
                    string? name = Marshal.PtrToStringUTF8(property.Name);
                    var mapping = Properties.FirstOrDefault(p => p.Native == name);
                    if (mapping is null) break;
                    status[mapping.Public] = property.Data == IntPtr.Zero || property.Format == 0 ? null : property.Format switch
                    {
                        1 => JsonValue.Create(Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(property.Data))),
                        3 => JsonValue.Create(Marshal.ReadInt32(property.Data) != 0),
                        4 => JsonValue.Create(Marshal.ReadInt64(property.Data)),
                        5 => FiniteValue(Marshal.PtrToStructure<double>(property.Data)),
                        _ => null,
                    };
                    if (mapping.Public == "paused" && status["state"]?.GetValue<string>() is "running" or "paused")
                        status["state"] = status["paused"]?.GetValue<bool>() == true ? "paused" : "running";
                    break;
            }
        }
        return (JsonObject)status.DeepClone();
    }

    public void Pause(bool paused) => Send("set", "pause", paused ? "yes" : "no");
    public void Seek(double seconds)
    {
        Contract.Require(double.IsFinite(seconds) && seconds >= 0 && seconds <= 315576000, "invalid_request", "Invalid seek position.");
        Send("seek", seconds.ToString("R", CultureInfo.InvariantCulture), "absolute+exact");
    }

    private void Send(params string[] arguments)
    {
        IntPtr[] strings = arguments.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
        IntPtr argv = Marshal.AllocCoTaskMem((strings.Length + 1) * IntPtr.Size);
        try
        {
            for (int i = 0; i < strings.Length; i++) Marshal.WriteIntPtr(argv, i * IntPtr.Size, strings[i]);
            Marshal.WriteIntPtr(argv, strings.Length * IntPtr.Size, IntPtr.Zero);
            lock (controlGate)
            {
                Contract.Require(player != IntPtr.Zero, "session_closed", "Native processing has stopped.");
                Check(command(player, (ulong)Interlocked.Increment(ref nextCommand), argv));
            }
        }
        finally { foreach (var text in strings) Marshal.FreeCoTaskMem(text); Marshal.FreeCoTaskMem(argv); }
    }

    private static string Quote(string path)
    {
        path = Path.GetFullPath(path).Replace('\\', '/');
        return "%" + Encoding.UTF8.GetByteCount(path) + "%" + path;
    }
    private static JsonNode? FiniteValue(double value) => double.IsFinite(value) ? JsonValue.Create(value) : null;
    private string Describe(int code) => Marshal.PtrToStringUTF8(errorText(code)) ?? "Native processing error.";
    private void Check(int code) { if (code < 0) throw new AddonException("native_error", Describe(code)); }
    public void Dispose()
    {
        lock (controlGate)
        {
            if (player == IntPtr.Zero) return;
            destroy(player); player = IntPtr.Zero; selectedFile.Dispose(); NativeLibrary.Free(library);
        }
    }

    private sealed record WatchedProperty(string Native, string Public, int Format);
    private static readonly WatchedProperty[] Properties = [
        new("time-pos", "positionSeconds", 5), new("duration", "durationSeconds", 5), new("pause", "paused", 3), new("seeking", "seeking", 3),
        new("video-params/w", "inputWidth", 4), new("video-params/h", "inputHeight", 4),
        new("video-out-params/w", "outputWidth", 4), new("video-out-params/h", "outputHeight", 4),
        new("video-out-params/pixelformat", "pixelFormat", 1), new("hwdec-current", "decoder", 1),
        new("decoder-frame-drop-count", "decoderDroppedFrames", 4), new("frame-drop-count", "outputDroppedFrames", 4),
    ];
    [StructLayout(LayoutKind.Sequential)] private struct Event { public int Id, Error; public ulong UserData; public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)] private struct Property { public IntPtr Name; public int Format; public IntPtr Data; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Create();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint ApiVersion();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Destroy(IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Initialize(IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Option(IntPtr context, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Observe(IntPtr context, ulong id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Command(IntPtr context, ulong id, IntPtr arguments);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr WaitEvent(IntPtr context, double timeout);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr ErrorText(int error);
}
