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
    private readonly SelectedFileStream? selectedSubtitles;
    private IntPtr player;
    private readonly Destroy destroy;
    private readonly Command command;
    private readonly WaitEvent wait;
    private readonly ErrorText errorText;
    private readonly JsonObject status = new() { ["state"] = "opening", ["paused"] = false };
    private long nextCommand;
    private readonly object controlGate = new();
    private readonly string streamMode, workDirectory;
    private readonly NativeEncoderPlan? encoderPlan;
    private readonly NativeEncoding? readinessEncoding;
    private int diagnosticCharacters;
    private long nextReadinessPoll;
    public bool Ended { get; private set; }
    public bool Failed { get; private set; }

    public NativePlayback(string installRoot, string? source, string configuration, string workDirectory, int slot, string backend,
        long sampleMapping = 0, NativeEncoding? encoding = null, int outputDescriptor = -1, Stream? sourceStream = null,
        OutputPlayback? playback = null, JsonObject? resolvedPlayback = null, Action? sourceCancel = null, Stream? externalSubtitles = null,
        string streamMode = "normal", NativeEncoding? readinessEncoding = null)
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8) throw new PlatformNotSupportedException("Native media currently requires Windows x64.");
        Contract.Require(sourceStream is null ? source is not null && Path.IsPathFullyQualified(source) && File.Exists(source)
            : source is null && sourceStream.CanRead, "invalid_source", "Choose one approved media source.");
        Contract.Require(slot is >= 1 and <= 9 or >= 1001 and <= 1003 or >= 1010 and <= 1013, "invalid_profile", "Unsupported native profile.");
        Contract.Require(backend is "DirectML" or "TensorRT", "invalid_profile", "Unsupported native backend.");
        encoding?.Validate();
        Contract.Require(encoding is null ? outputDescriptor == -1 : outputDescriptor >= 0, "native_output", "Invalid native output configuration.");
        installRoot = Path.GetFullPath(installRoot);
        this.streamMode = streamMode; this.workDirectory = workDirectory;
        this.readinessEncoding = readinessEncoding;
        status["selectedBackend"] = backend;
        library = NativeRuntime.Load(installRoot);
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
            Check(Load<RequestLogs>("mpv_request_log_messages")(player, "info"));
            NativeEncoderPlan? encoder = encoding is not null || readinessEncoding is not null
                ? NativeEncoderPlan.Select(encoding ?? readinessEncoding!, NativeEncoderPlan.AdapterVendors()) : null;
            encoderPlan = encoder;
            if (encoder is not null) status["encoder"] = encoder.ToJson();
            foreach (string name in new[] { "config", "load-scripts", "osc", "ytdl", "load-stats-overlay", "load-console",
                "load-auto-profiles", "load-select", "load-positioning", "load-commands", "load-context-menu",
                "input-default-bindings", "input-terminal", "terminal", "audio", "sub", "access-references" }) Set(name, "no");
            Set("sub-auto", "no"); Set("audio-file-auto", "no"); Set("cover-art-auto", "no");
            Set("vo", "null"); Set("hwdec", backend == "DirectML" ? "d3d11va" : "nvdec"); Set("idle", "yes");
            if (encoding is null)
            {
                // Opt in only for our discard sink. Older native previews used
                // this behavior globally and do not expose the new option.
                int discard = option(player, "vo-null-accept-hwframes", "yes");
                if (discard != -5) Check(discard); // MPV_ERROR_OPTION_NOT_FOUND
            }
            if (encoding is not null)
            {
                Set("o", "pipe:" + outputDescriptor.ToString(CultureInfo.InvariantCulture));
                Set("of", encoding.Container == "fragmentedMp4" ? "mp4" : encoding.Container);
                Set("ofopts", encoding.Container switch {
                    "matroska" => "live=1,cluster_time_limit=1000,flush_packets=1",
                    "fragmentedMp4" => "movflags=frag_keyframe+empty_moov+default_base_moof,frag_duration=1000000,flush_packets=1",
                    _ => "mpegts_flags=+resend_headers,flush_packets=1",
                });
                Set("ovc", encoder!.Codec); Set("ovc-hwframes", "no");
                Set("ovcopts", encoder.Options);
                if (encoding.KeyframeSeconds > 0) Set("ovc-keyframe-seconds", encoding.KeyframeSeconds.ToString("R", CultureInfo.InvariantCulture));
                Set("ocopy-metadata", "no");
                if (encoding.AudioCodec != "none" && playback?.AudioTrack != "none")
                {
                    Set("audio", resolvedPlayback?["audioTrack"]?["typeOrdinal"]?.GetValue<long>().ToString(CultureInfo.InvariantCulture) ?? "auto");
                    Set("oac", encoding.AudioCodec == "opus" ? "libopus" : "aac");
                    Set("oacopts", "b=" + (encoding.AudioKbps * 1000).ToString(CultureInfo.InvariantCulture));
                    if (encoding.AudioChannels == 2) Set("audio-channels", "stereo");
                }
                if (encoding.LengthSeconds > 0) Set("length", encoding.LengthSeconds.ToString("R", CultureInfo.InvariantCulture));
                status["encoding"] = encoding.ToJson();
                if (playback is not null)
                {
                    if (playback.StartSeconds > 0) Set("start", playback.StartSeconds.ToString("R", CultureInfo.InvariantCulture));
                    Set("hr-seek", "yes"); Set("hr-seek-framedrop", "no");
                    status["playback"] = resolvedPlayback?.DeepClone();
                    if (playback.SubtitleMode == "burn")
                    {
                        string sid = externalSubtitles is not null ? resolvedPlayback!["externalSubtitleOrdinal"]!.GetValue<int>().ToString(CultureInfo.InvariantCulture)
                            : resolvedPlayback!["subtitleTrack"]!["typeOrdinal"]!.GetValue<long>().ToString(CultureInfo.InvariantCulture);
                        Set("sub", sid); Set("sub-visibility", "yes"); Set("sub-ass", "yes"); Set("embeddedfonts", "yes");
                        if (externalSubtitles is not null) Set("sub-files", "ajnsubtitle://media");
                    }
                }
            }
            // Use our already-opened file, restrict container parsers, and deny
            // nested protocol opens. Playlists and external references cannot
            // turn a selected file into additional file/network access.
            Set("demuxer", "lavf");
            const string formats = "matroska,webm,mov,avi,mpegts,srt,ass,webvtt";
            const string protocols = "ajnselected,ajnsubtitle";
            Set("demuxer-lavf-o", "protocol_whitelist=%" + protocols.Length + "%" + protocols + ",format_whitelist=%" + formats.Length + "%" + formats);
            string inference = Path.Combine(installRoot, "animejanai", "inference");
            var parameters = new Dictionary<string, string>
            {
                ["lib"] = Path.Combine(inference, "aji.dll"), ["conf"] = configuration,
                ["model-dir"] = Path.Combine(installRoot, "animejanai", "onnx"),
                ["rife-model-dir"] = Path.Combine(installRoot, "animejanai", "rife"),
                ["trtexec"] = Path.Combine(inference, "trtexec.exe"), ["stats"] = Path.Combine(workDirectory, "inference.log"),
            };
            string filters = "@aji:animejanai:" + string.Join(':', parameters.Select(p => p.Key + "=" + Quote(p.Value))) + ":slot=" + slot;
            if (streamMode != "normal") filters += ":stream-mode=" + streamMode;
            if (encoding is not null) filters += ",format=fmt=" + encoder!.PixelFormat + ":convert=yes";
            if (sampleMapping > 0) filters += ",@ajn-sample:ajn-sample:mapping=" + sampleMapping.ToString(CultureInfo.InvariantCulture);
            Set("vf", filters);
            selectedFile = sourceStream is null ? new SelectedFileStream(source!)
                : new SelectedFileStream(sourceStream, sourceCancel ?? (sourceStream is RemoteMediaStream remote ? remote.Cancel : null));
            Check(selectedFile.Register(library, player));
            if (externalSubtitles is not null)
            {
                selectedSubtitles = new SelectedFileStream(externalSubtitles, selectedUri: "ajnsubtitle://media");
                Check(selectedSubtitles.Register(library, player));
            }
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
            selectedSubtitles?.Dispose();
            throw;
        }
    }

    public JsonObject Poll()
    {
        if (!Ended && streamMode is "prepare" or "check" && Environment.TickCount64 >= nextReadinessPoll)
        {
            nextReadinessPoll = Environment.TickCount64 + 100;
            Send("vf-command", "aji", "poll", "");
        }
        for (int i = 0; i < 256; i++)
        {
            var message = Marshal.PtrToStructure<Event>(wait(player, i == 0 ? .05 : 0));
            if (message.Id == 0) break;
            switch (message.Id)
            {
                case 2: ObserveLog(Marshal.PtrToStructure<LogMessage>(message.Data)); break;
                case 1: Ended = true; break;
                case 5:
                    status["lastCommandId"] = message.UserData;
                    status["lastCommandError"] = message.Error < 0 ? Describe(message.Error) : null;
                    break;
                case 7:
                    int reason = Marshal.ReadInt32(message.Data);
                    int error = Marshal.ReadInt32(message.Data, 4);
                    Ended = true; Failed |= reason is 4 or 5 || error < 0;
                    status["state"] = Failed ? "failed" : "completed";
                    if (Failed) {
                        status["error"] ??= reason == 5 ? "Playlist redirection is not a selected media source." : Describe(error);
                        status["errorCode"] ??= "native_processing_failed";
                    }
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
            if (Ended) break;
        }
        return (JsonObject)status.DeepClone();
    }

    private void ObserveLog(LogMessage message)
    {
        string prefix = Marshal.PtrToStringUTF8(message.Prefix) ?? "";
        string level = Marshal.PtrToStringUTF8(message.Level) ?? "";
        string text = Marshal.PtrToStringUTF8(message.Text) ?? "";
        if (text.Length > 4096) return;
        // Operator-only diagnostics. The supervisor keeps a bounded private
        // log; none of this text enters the addon-visible status contract.
        if (level is "error" or "fatal" && diagnosticCharacters < 8192)
        {
            string line = (prefix + ": " + text).Replace('\0', ' ');
            Console.Error.WriteLine(line[..Math.Min(line.Length, 8192 - diagnosticCharacters)]);
            diagnosticCharacters += line.Length;
        }
        if (prefix is "animejanai" or "vf/animejanai")
        {
            var evidence = NativeProcessingEvidence.Parse(text);
            if (evidence is not null)
            {
                evidence["activeModels"] = NativeProcessingEvidence.Models(workDirectory);
                status["processing"] = evidence;
                string state = evidence["state"]!.GetValue<string>();
                if (state is "engineMissing" or "engineIncompatible" or "preparationFailed" or "failed")
                    Fail(state switch { "engineMissing" => "engine_missing", "engineIncompatible" => "engine_incompatible", "preparationFailed" => "preparation_failed", _ => "ai_filter_failed" });
                else if (streamMode is "prepare" or "check")
                {
                    if (state == "building") status["state"] = "building";
                    else
                    {
                        if (encoderPlan is not null && readinessEncoding is not null)
                        {
                            Contract.Require(NativeLibrary.TryGetExport(library, "mpv_ajn_encoder_check_v1", out var address), "native_update_required", "Native encoder readiness requires the matching player runtime.");
                            var probe = Marshal.GetDelegateForFunctionPointer<EncoderCheck>(address);
                            int result = probe(encoderPlan.Codec, encoderPlan.Options, evidence["outputWidth"]!.GetValue<int>(), evidence["outputHeight"]!.GetValue<int>(), readinessEncoding.BitDepth, evidence["outputFrameRate"]!.GetValue<double>());
                            if (result < 0) { Fail(result == -12 ? "resource_exhausted" : "encoder_unavailable"); return; }
                            status["encoder"]!["validation"] = "openedForOutputFormat";
                        }
                        status["state"] = "completed"; Ended = true;
                    }
                }
                return;
            }
            // The backend logs its detailed error before the filter emits the
            // typed streaming result. Keep consuming events so engine_missing
            // and engine_incompatible are not replaced by a generic failure.
            // Required filters are terminal on failure in the native chain.
            if (level is "error" or "fatal")
            {
                if (text.Contains("AJN_STREAM_POLICY_UNAVAILABLE", StringComparison.Ordinal)) Fail("native_update_required");
                else status["errorCode"] ??= "ai_filter_failed";
            }
        }
        else if (level is "error" or "fatal" && (text.Contains("_nvenc", StringComparison.Ordinal) || text.Contains("_amf", StringComparison.Ordinal))) Fail("encoder_failed");
        else if (prefix == "vf" && text.Contains("Disabling filter aji", StringComparison.Ordinal)) Fail("ai_filter_failed");
    }
    private void Fail(string code)
    {
        Failed = Ended = true; status["state"] = "failed"; status["errorCode"] = code;
        status["error"] = "The selected native processing pipeline could not complete.";
        if (code == "ai_filter_failed" && status["processing"] is JsonObject processing) processing["state"] = "failed";
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
            destroy(player); player = IntPtr.Zero; selectedFile.Dispose(); selectedSubtitles?.Dispose();
        }
    }

    private sealed record WatchedProperty(string Native, string Public, int Format);
    private static readonly WatchedProperty[] Properties = [
        new("time-pos", "positionSeconds", 5), new("duration", "durationSeconds", 5), new("pause", "paused", 3), new("seeking", "seeking", 3),
        new("video-params/w", "inputWidth", 4), new("video-params/h", "inputHeight", 4),
        new("video-out-params/w", "outputWidth", 4), new("video-out-params/h", "outputHeight", 4),
        new("video-out-params/pixelformat", "pixelFormat", 1), new("hwdec-current", "decoder", 1),
        new("video-out-params/aspect", "displayAspectRatio", 5), new("container-fps", "containerFrameRate", 5),
        new("video-out-params/par", "pixelAspectRatio", 5), new("video-params/par", "inputPixelAspectRatio", 5),
        new("audio-params/channel-count", "inputAudioChannels", 4), new("audio-params/samplerate", "inputAudioSampleRate", 4),
        new("audio-out-params/channel-count", "audioChannels", 4), new("audio-out-params/samplerate", "audioSampleRate", 4),
        new("audio-out-params/channels", "audioLayout", 1),
        new("decoder-frame-drop-count", "decoderDroppedFrames", 4), new("frame-drop-count", "outputDroppedFrames", 4),
    ];
    [StructLayout(LayoutKind.Sequential)] private struct Event { public int Id, Error; public ulong UserData; public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)] private struct Property { public IntPtr Name; public int Format; public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)] private struct LogMessage { public IntPtr Prefix, Level, Text; public int LogLevel; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RequestLogs(IntPtr player, [MarshalAs(UnmanagedType.LPUTF8Str)] string level);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EncoderCheck([MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string options, int width, int height, int bitDepth, double fps);
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
