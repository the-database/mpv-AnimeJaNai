using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons.Native;

internal static class NativeProbeWorker
{
    internal static async Task<int> RunAsync(bool subtitles = false)
    {
        var input = Console.OpenStandardInput(); var output = Console.OpenStandardOutput();
        var channel = new MessageChannel(input, output);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(subtitles ? 300 : 30));
        try
        {
            byte[] gate = new byte[1]; if (await input.ReadAsync(gate, deadline.Token) != 1 || gate[0] != 1) return 2;
            var launch = await channel.ReadAsync(deadline.Token);
            Contract.Require(Contract.Number(launch, "version") == 1, "native_protocol", "Unsupported probe protocol.");
            string root = Contract.Text(launch, "root", 4096);
            Contract.Require((launch["source"] is null) != (launch["remoteSource"] is null), "invalid_source", "Choose one approved source.");
            string? path = launch["source"] is null ? null : Contract.Text(launch, "source", 4096);
            if (path is not null) SafeFiles.CheckParents(path);
            using Stream source = path is not null ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
                : await RemoteMediaStream.OpenAsync(RemoteInputPlan.FromPrivateJson(launch["remoteSource"]!.AsObject()), deadline.Token);
            using var cancelled = deadline.Token.Register(() => { if (source is RemoteMediaStream remote) remote.Cancel(); });
            string identity = source is RemoteMediaStream remoteSource ? remoteSource.RepresentationId : LocalIdentity(path!, source);
            using var replay = subtitles && !source.CanSeek ? new ProbeReplayStream(source) : null;
            var result = Probe(root, replay ?? source, identity, deadline.Token);
            byte[] bytes;
            if (subtitles)
            {
                Contract.Require(launch["subtitles"] is JsonObject, "native_protocol", "Expected subtitle extraction options.");
                var options = SubtitleRequest.Parse(launch["subtitles"]!.AsObject());
                int index = options.Resolve(result);
                if (replay is not null) replay.BeginReplay(); else source.Position = 0;
                bytes = NativeSubtitles.Extract(root, replay ?? source, index, options, deadline.Token);
            }
            else
            {
                bytes = Encoding.UTF8.GetBytes(result.ToJsonString());
                Contract.Require(bytes.Length <= 262144, "probe_result_too_large", "Probe result exceeds 256 KiB.");
            }
            for (int offset = 0; offset < bytes.Length; offset += 32768)
                await channel.WriteAsync(new JsonObject { ["version"] = 1, ["offset"] = offset,
                    ["data"] = Convert.ToBase64String(bytes.AsSpan(offset, Math.Min(32768, bytes.Length - offset))) }, deadline.Token);
            await channel.WriteAsync(new JsonObject { ["version"] = 1, ["complete"] = true, ["bytes"] = bytes.Length,
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)) }, deadline.Token);
            return 0;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await channel.WriteAsync(new JsonObject { ["version"] = 1, ["error"] = e is AddonException a ? a.Code : e is OperationCanceledException ? "probe_cancelled" : "probe_failed" }, closing.Token); }
            catch (Exception) { }
            return 1;
        }
    }
    internal static string LocalIdentity(string path, Stream source)
    {
        // FileShare.Read holds the source stable for the probe. File identity,
        // length/write time and bounded header/tail fingerprints detect normal
        // replacement/editing without hashing an entire episode before playback.
        var info = new FileInfo(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFullPath(path) + "\n" + source.Length + "\n" + info.CreationTimeUtc.Ticks + "\n" + info.LastWriteTimeUtc.Ticks));
        long position = source.Position; byte[] buffer = new byte[65536];
        foreach (long offset in new[] { 0L, Math.Max(0, source.Length - buffer.Length) })
        {
            source.Position = offset; int wanted = (int)Math.Min(buffer.Length, source.Length - offset);
            source.ReadExactly(buffer.AsSpan(0, wanted)); hash.AppendData(buffer.AsSpan(0, wanted));
        }
        source.Position = position; return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    internal static JsonObject Probe(string root, Stream source, string identity, CancellationToken token, NativeReadMetrics? metrics = null)
    {
        IntPtr library = NativeRuntime.Load(root);
        Contract.Require(NativeLibrary.TryGetExport(library, "mpv_ajn_probe_v1", out var address) &&
            NativeLibrary.TryGetExport(library, "mpv_ajn_probe_free_v1", out _), "feature_unavailable", "The native probe ABI is unavailable.");
        var probe = Marshal.GetDelegateForFunctionPointer<ProbeFunction>(address);
        var free = Marshal.GetDelegateForFunctionPointer<FreeFunction>(NativeLibrary.GetExport(library, "mpv_ajn_probe_free_v1"));
        const long maximumReadBytes = 64L * 1024 * 1024;
        var reader = new NativeReadBuffer(source, token, maximumReadBytes, metrics: metrics);
        ReadFunction read = (_, destination, length) =>
        {
            try
            {
                return reader.Read(destination, length);
            }
            catch { return -5; }
        };
        SeekFunction seek = (_, offset, whence) =>
        {
            try
            {
                if (token.IsCancellationRequested) return -5;
                if ((whence & 0x10000) != 0) return source.Length; // AVSEEK_SIZE
                if (!source.CanSeek) return -38;
                whence &= ~0x20000; // AVSEEK_FORCE
                return whence is >= 0 and <= 2 ? source.Seek(offset, (SeekOrigin)whence) : -22;
            }
            catch { return -5; }
        };
        CancelFunction cancel = _ => token.IsCancellationRequested || reader.BytesRead >= maximumReadBytes ? 1 : 0;
        IntPtr json = IntPtr.Zero;
        try
        {
            int result = probe(new IntPtr(1), read, seek, cancel, source.CanSeek ? 1 : 0, out json);
            token.ThrowIfCancellationRequested();
            Contract.Require(result >= 0 && json != IntPtr.Zero, "probe_failed", "The selected container could not be probed within its read budget.");
            string text = Marshal.PtrToStringUTF8(json)!;
            var metadata = Contract.ParseObject(Encoding.UTF8.GetBytes(text));
            metadata["representationId"] = identity;
            foreach (var track in metadata["tracks"]!.AsArray()) track!["trackId"] = identity + ":" + track["streamIndex"]!.GetValue<long>();
            return metadata;
        }
        finally { if (json != IntPtr.Zero) free(json); GC.KeepAlive(read); GC.KeepAlive(seek); GC.KeepAlive(cancel); }
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReadFunction(IntPtr opaque, IntPtr target, int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long SeekFunction(IntPtr opaque, long offset, int whence);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CancelFunction(IntPtr opaque);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ProbeFunction(IntPtr opaque, ReadFunction read, SeekFunction seek, CancelFunction cancel, int seekable, out IntPtr json);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeFunction(IntPtr json);
}
