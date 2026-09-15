using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

namespace AnimeJaNai.Addons.Native;

internal static class MediaWorker
{
    // Internal entry point. The supervisor grants execution by sending the gate
    // byte only after attaching this process to its lifetime/resource job.
    public static async Task<int> RunAsync()
    {
        try { return await RunCoreAsync(); }
        catch (Exception error)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await new MessageChannel(Stream.Null, Console.OpenStandardOutput()).WriteAsync(new JsonObject
            {
                ["version"] = 1, ["status"] = new JsonObject { ["state"] = "failed", ["errorCode"] = error is AddonException addon ? addon.Code : "native_worker_failed",
                    ["error"] = "Native processing could not complete. Check the selected source, profile and supported formats." },
            }, deadline.Token);
            return 1;
        }
    }

    private static async Task<int> RunCoreAsync()
    {
        var input = Console.OpenStandardInput();
        byte[] gate = new byte[1];
        if (await input.ReadAsync(gate) != 1 || gate[0] != 1) return 2;
        var channel = new MessageChannel(input, Console.OpenStandardOutput());
        using var lifetime = new CancellationTokenSource();
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var launch = await channel.ReadAsync(startup.Token);
        Contract.Require(Contract.Number(launch, "version") == 1, "native_version", "Unsupported internal media protocol.");
        long sampleHandle = launch["sampleMapping"] is null ? 0 : Contract.Number(launch, "sampleMapping");
        Contract.Require(sampleHandle >= 0, "native_protocol", "Invalid private sample mapping.");
        // The handle was duplicated into this process before the execution gate.
        // It outlives libmpv and is closed after the filter unmaps its view.
        using var sampleMapping = new SafeFileHandle(new IntPtr(sampleHandle), ownsHandle: true);
        long outputHandle = launch["outputHandle"] is null ? 0 : Contract.Number(launch, "outputHandle");
        var served = launch["servedOutput"] is JsonObject streamPlan ? ServedOutputPlan.Parse(streamPlan) : null;
        Contract.Require(outputHandle >= 0 && (served is null ? (outputHandle == 0) == (launch["encoding"] is null) :
            outputHandle == 0 && launch["encoding"] is JsonObject), "native_protocol", "Invalid encoded output launch.");
        using var outputMapping = new SafeFileHandle(new IntPtr(outputHandle), ownsHandle: true);
        using var encodedWriter = outputHandle == 0 ? null : new NativeEncodedWriter(outputMapping);
        var encoding = launch["encoding"] is JsonObject options ? NativeEncoding.Parse(options) : null;
        Contract.Require(served is not null || (encodedWriter is null) == (encoding is null), "native_protocol", "Invalid encoding parameters.");
        Contract.Require((launch["source"] is null) != (launch["remoteSource"] is null), "invalid_source", "Choose one approved media source.");
        using var remote = launch["remoteSource"] is JsonObject remotePlan
            ? await RemoteMediaStream.OpenAsync(RemoteInputPlan.FromPrivateJson(remotePlan), startup.Token) : null;
        string installRoot = Contract.Text(launch, "root", 4096);
        string? localPath = launch["source"] is null ? null : Contract.Text(launch, "source", 4096);
        var playback = launch["playback"] is JsonObject playbackOptions ? OutputPlayback.Parse(playbackOptions) : null;
        string? subtitlePath = launch["subtitleSource"] is null ? null : Contract.Text(launch, "subtitleSource", 4096);
        var remoteSubtitlePlan = launch["remoteSubtitles"] is JsonObject remoteSubtitle ? RemoteInputPlan.FromPrivateJson(remoteSubtitle) : null;
        Contract.Require((subtitlePath is null) == (playback?.ExternalSourceId is null), "native_protocol", "External subtitle selection is incomplete.");
        Contract.Require((remoteSubtitlePlan is null) == (playback?.ExternalRemoteSource is null) && (subtitlePath is null || remoteSubtitlePlan is null),
            "native_protocol", "Remote subtitle selection is incomplete or ambiguous.");
        if (subtitlePath is not null)
        {
            SafeFiles.CheckParents(subtitlePath);
            Contract.Require(new[] { ".srt", ".ass", ".ssa", ".vtt" }.Contains(Path.GetExtension(subtitlePath).ToLowerInvariant()),
                "subtitle_format_unavailable", "External subtitles currently support SRT, ASS/SSA and WebVTT.");
        }
        async Task<Stream?> OpenSubtitles()
        {
            if (remoteSubtitlePlan is null) return subtitlePath is null ? null : new FileStream(subtitlePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var downloading = ExternalSubtitleInput.DownloadAsync(remoteSubtitlePlan, deadline.Token);
            try
            {
                while (!downloading.IsCompleted)
                {
                    await channel.WriteAsync(new JsonObject { ["version"] = 1, ["status"] = new JsonObject { ["state"] = "loadingSubtitles" } }, deadline.Token);
                    if (await Task.WhenAny(downloading, Task.Delay(TimeSpan.FromSeconds(5), deadline.Token)) == downloading) break;
                }
                var result = await downloading;
                try
                {
                    var probe = NativeProbeWorker.Probe(installRoot, result, new string('0', 64), deadline.Token);
                    Contract.Require(probe["tracks"] is JsonArray tracks && tracks.Count == 1 && tracks[0]?["type"]?.GetValue<string>() == "subtitle" &&
                        tracks[0]?["codec"]?.GetValue<string>() is "ass" or "ssa" or "subrip" or "webvtt",
                        "subtitle_format_unavailable", "External remote subtitles support standalone SRT, ASS/SSA and WebVTT.");
                    result.Position = 0; return result;
                }
                catch { result.Dispose(); throw; }
            }
            catch
            {
                deadline.Cancel(); try { (await downloading).Dispose(); } catch { }
                throw;
            }
        }
        using Stream? externalSubtitles = await OpenSubtitles();
        Contract.Require(externalSubtitles is null || externalSubtitles.Length is > 0 and <= 16 * 1024 * 1024,
            "subtitle_limit", "External subtitle resource exceeds 16 MiB or is empty.");
        Contract.Require(playback is null || encoding is not null, "native_protocol", "Playback controls require encoding.");
        Contract.Require(served is null || playback is not null, "native_protocol", "Served output requires explicit playback settings.");
        if (playback is not null && localPath is not null) SafeFiles.CheckParents(localPath);
        using Stream? local = playback is not null && localPath is not null ? new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        Stream? source = remote ?? local;
        using var replay = playback is not null && source?.CanSeek == false ? new ProbeReplayStream(source) : null;
        JsonObject? resolvedPlayback = null;
        if (playback is not null)
        {
            Contract.Require(playback.StartSeconds == 0 || source!.CanSeek, "input_not_seekable", "This input cannot seek to the requested start position.");
            using var probing = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var cancelProbe = probing.Token.Register(() => remote?.Cancel());
            string identity = remote?.RepresentationId ?? NativeProbeWorker.LocalIdentity(localPath!, source!);
            await channel.WriteAsync(new JsonObject { ["version"] = 1, ["status"] = new JsonObject { ["state"] = "probing" } }, probing.Token);
            var work = Task.Run(() => NativeProbeWorker.Probe(installRoot, replay ?? source!, identity, probing.Token));
            JsonObject probe;
            try
            {
                while (!work.IsCompleted)
                {
                    if (await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(5))) == work) break;
                    await channel.WriteAsync(new JsonObject { ["version"] = 1, ["status"] = new JsonObject { ["state"] = "probing" } }, probing.Token);
                }
                probe = await work;
            }
            finally
            {
                // Keep source callbacks alive until the native call returns. If it hangs,
                // the supervising job terminates this worker after missed heartbeats.
                if (!work.IsCompleted) probing.Cancel();
                try { await work; } catch { }
            }
            resolvedPlayback = playback.Resolve(probe, encoding!.AudioCodec);
            if (served is not null) StreamRequest.ValidateSource(probe);
            if (playback.SubtitleMode == "burn")
            {
                var attachments = probe["attachments"]!.AsArray();
                Contract.Require(attachments.Count <= 64 && attachments.OfType<JsonObject>().Sum(a => a["byteLength"]!.GetValue<long>()) <= 32 * 1024 * 1024 &&
                    attachments.OfType<JsonObject>().All(a => a["byteLength"]!.GetValue<long>() <= 16 * 1024 * 1024),
                    "subtitle_limit", "Embedded attachments exceed subtitle rendering limits.");
                if (externalSubtitles is not null)
                    resolvedPlayback["externalSubtitleOrdinal"] = probe["tracks"]!.AsArray().Count(t => t?["type"]?.GetValue<string>() == "subtitle") + 1;
                resolvedPlayback["composition"] = "softwareAfterProcessing";
            }
            if (replay is not null) { replay.BeginReplay(); source = replay; }
            else source!.Position = 0;
        }
        using var remuxLifetime = new CancellationTokenSource();
        using var remuxPipe = served is null ? null : new NativeEncodedPipe();
        using var self = Process.GetCurrentProcess();
        using var remuxHandle = new SafeFileHandle(new IntPtr(remuxPipe?.DuplicateTo(self) ?? 0), ownsHandle: true);
        using var remuxWriter = remuxPipe is null ? null : new NativeEncodedWriter(remuxHandle);
        var demandGate = served is null ? null : new MuxDemandGate(playback!.StartSeconds);
        using var muxer = served is null ? null : new NativeMuxer(served.Directory, remuxPipe!.Reader, remuxLifetime.Token) { Demand = demandGate };
        if (served is not null) Environment.CurrentDirectory = served.Directory;
        var processingEncoding = served is null ? encoding : encoding! with { Container = "matroska", KeyframeFrames = 600, KeyframeSeconds = served.SegmentSeconds };
        using var player = new NativePlayback(installRoot, source is null ? localPath : null,
            Contract.Text(launch, "configuration", 4096), Contract.Text(launch, "work", 4096),
            checked((int)Contract.Number(launch, "slot")), Contract.Text(launch, "backend", 32), sampleHandle, processingEncoding,
            remuxWriter?.Descriptor ?? encodedWriter?.Descriptor ?? -1, source,
            playback, resolvedPlayback, remote is null ? null : remote.Cancel, externalSubtitles);
        Task muxing = muxer is null ? Task.CompletedTask : Task.Run(() => muxer.Run(installRoot, served!.Container, served.Segmented, served.SegmentSeconds));
        if (remuxPipe is not null)
            _ = muxing.ContinueWith(t => { _ = t.Exception; remuxPipe.Dispose(); }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        int flushing = 0;
        Task flush = Task.CompletedTask;
        try
        {
        var commands = Task.Run(async () =>
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    var control = await channel.ReadAsync(lifetime.Token);
                    lifetime.Token.ThrowIfCancellationRequested();
                    switch (Contract.Text(control, "operation", 32))
                    {
                        case "pause":
                            Contract.Require(control["paused"] is JsonValue p && p.TryGetValue<bool>(out _), "invalid_request", "Invalid pause state.");
                            demandGate?.Pause(control["paused"]!.GetValue<bool>());
                            if (Volatile.Read(ref flushing) == 0)
                            {
                                try { player.Pause(control["paused"]!.GetValue<bool>()); }
                                catch (AddonException error) when (error.Code == "session_closed" && Volatile.Read(ref flushing) != 0) { }
                            }
                            break;
                        case "stream-demand":
                            Contract.Require(demandGate is not null && control["seconds"] is JsonValue demand && demand.TryGetValue<double>(out _), "invalid_request", "Invalid native stream demand.");
                            demandGate.SetDemand(control["seconds"]!.GetValue<double>()); break;
                        case "seek":
                            Contract.Require(encoding is null, "operation_unavailable", "Encoded output seeking requires closing and reopening at an offset.");
                            Contract.Require(control["seconds"] is JsonValue s && s.TryGetValue<double>(out _), "invalid_request", "Invalid seek position.");
                            player.Seek(control["seconds"]!.GetValue<double>()); break;
                        default: throw new AddonException("unknown_method", "Unknown internal media control.");
                    }
                }
            }
            catch (AddonException error) when (error.Code == "worker_exited") { }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        });
        JsonObject? terminalState = null;
        bool sourceFailed = false, muxFailed = false;
        try
        {
            long last = 0;
            while (!commands.IsCompleted)
            {
                var state = Volatile.Read(ref flushing) == 0 ? player.Poll() : (JsonObject)terminalState!.DeepClone();
                if (served is not null)
                {
                    state["encoding"] = encoding!.ToJson();
                    state["muxing"] = new JsonObject { ["container"] = served.Container, ["segmented"] = served.Segmented };
                    state["buffer"] = demandGate!.Status();
                    muxFailed = muxing.IsFaulted || muxing.IsCanceled || flush.IsFaulted;
                    if (muxFailed)
                    {
                        state["state"] = "failed"; state["error"] = "Native stream muxing failed.";
                        state["errorCode"] = (muxing.Exception?.GetBaseException() ?? flush.Exception?.GetBaseException()) is AddonException addon ? addon.Code : "native_mux_failed";
                    }
                }
                if (remote is not null)
                {
                    var inputStatus = remote.Status();
                    string? inputError = inputStatus["errorCode"]?.GetValue<string>();
                    sourceFailed = inputError is not null and not "input_cancelled";
                    if (player.Ended && !player.Failed && inputError == "input_cancelled") inputStatus["errorCode"] = null;
                    state["input"] = inputStatus;
                    if (sourceFailed) { state["state"] = "failed"; state["error"] = "Remote media input failed: " + inputError; }
                }
                if ((player.Ended || sourceFailed || muxFailed) && encoding is not null)
                {
                    terminalState ??= (JsonObject)state.DeepClone();
                    // Encoder and muxer may still hold delayed packets. Do not
                    // publish completion before their destruction flushes them.
                    state["state"] = "finishing";
                }
                if (served is not null && player.Ended && !sourceFailed && !muxFailed && Interlocked.CompareExchange(ref flushing, 1, 0) == 0)
                {
                    // Flushing may meet demand backpressure. Keep the control channel and
                    // heartbeats live so a long pause can resume the final buffered packets.
                    flush = Task.Run(() => { player.Dispose(); remuxWriter!.Dispose(); });
                }
                if (Stopwatch.GetElapsedTime(last) >= TimeSpan.FromMilliseconds(250) || player.Ended || sourceFailed || muxFailed)
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await channel.WriteAsync(new JsonObject { ["version"] = 1, ["status"] = state }, deadline.Token);
                    last = Stopwatch.GetTimestamp();
                }
                if (sourceFailed || muxFailed || player.Ended && (served is null || flush.IsCompleted && muxing.IsCompleted)) break;
                if (Volatile.Read(ref flushing) != 0) await Task.Delay(50);
            }
        }
        finally
        {
            lifetime.Cancel();
            // Console's redirected input may be backed by a synchronous Windows
            // handle. EOF closes promptly when the parent stops us, but a natural
            // media EOF must not wait forever for another control message.
            try { await commands.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch (TimeoutException) { _ = commands.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); }
        }
        if (served is not null && (!muxing.IsCompleted || sourceFailed || muxFailed))
        {
            remuxLifetime.Cancel(); remuxPipe!.Dispose();
        }
        await flush;
        player.Dispose();
        encodedWriter?.Dispose();
        remuxWriter?.Dispose();
        if (served is not null)
        {
            try { await muxing.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (Exception error)
            {
                remuxLifetime.Cancel(); remuxPipe!.Dispose(); muxFailed = true;
                terminalState ??= new JsonObject(); terminalState["state"] = "failed";
                terminalState["errorCode"] = error is AddonException addon ? addon.Code : "native_mux_failed";
                terminalState["error"] = "The native stream muxer could not complete its output.";
            }
        }
        if (terminalState is not null)
        {
            if (demandGate is not null) terminalState["buffer"] = demandGate.Status();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await channel.WriteAsync(new JsonObject { ["version"] = 1, ["status"] = terminalState }, deadline.Token);
        }
        return player.Failed || sourceFailed || muxFailed ? 1 : 0;
        }
        finally
        {
            lifetime.Cancel(); remuxLifetime.Cancel(); remuxPipe?.Dispose();
            // Never dispose native callback state while the muxer or encoder flush
            // is still executing. The parent job bounds a non-cooperative shutdown.
            try { await flush; } catch { }
            try { await muxing; } catch { }
        }
    }
}
