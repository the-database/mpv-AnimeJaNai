using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons.Native;

internal static class NativeProbeProcess
{
    internal static async Task<JsonObject> RunAsync(string root, string? source, RemoteInputPlan? remote, string workRoot, WorkerCommand command, CancellationToken token)
    {
        var result = Contract.ParseObject(await RunPayloadAsync(root, source, remote, workRoot, command, token));
        Contract.Require(Contract.Number(result, "probeAbi") == 1 && result["tracks"] is JsonArray { Count: <= 128 }, "native_protocol", "Invalid native probe result.");
        return result;
    }
    internal static async Task<byte[]> RunPayloadAsync(string root, string? source, RemoteInputPlan? remote, string workRoot, WorkerCommand command, CancellationToken token, SubtitleRequest? subtitles = null)
    {
        token.ThrowIfCancellationRequested();
        Contract.Require((source is null) != (remote is null), "invalid_source", "Choose one approved probe source.");
        remote?.Validate();
        string directory = WorkerBridge.CreateWorkDirectory(workRoot, "media");
        string actualRoot = Path.GetDirectoryName(directory)!;
        Process? process = null; Task diagnostics = Task.CompletedTask;
        WindowsJob? job = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(subtitles is null ? 30 : 300));
        try
        {
            job = new WindowsJob(new UIntPtr(512UL << 20), new UIntPtr(1024UL << 20), cpuRate: 0);
            var info = WorkerBridge.ProcessInfo(command.Executable, directory);
            foreach (string argument in command.PrefixArguments.Append(subtitles is null ? "probe-worker" : "subtitle-worker")) info.ArgumentList.Add(argument);
            process = Process.Start(info) ?? throw new AddonException("probe_failed", "Could not start the native probe.");
            job.Attach(process);
            diagnostics = DrainAsync(process.StandardError.BaseStream);
            var channel = new MessageChannel(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
            await process.StandardInput.BaseStream.WriteAsync(new byte[] { 1 }, deadline.Token);
            await channel.WriteAsync(new JsonObject { ["version"] = 1, ["root"] = Path.GetFullPath(root), ["source"] = source,
                ["remoteSource"] = remote?.ToPrivateJson(), ["subtitles"] = subtitles?.ToJson() }, deadline.Token);
            using var buffer = new MemoryStream();
            while (true)
            {
                var message = await channel.ReadAsync(deadline.Token);
                Contract.Require(Contract.Number(message, "version") == 1, "native_protocol", "Invalid probe protocol version.");
                if (message["error"] is not null)
                {
                    string code = Contract.Text(message, "error", 64);
                    Contract.Require(Contract.ValidKey(code), "native_protocol", "Invalid probe error.");
                    throw new AddonException(code, "The approved source could not be probed.");
                }
                if (message["complete"]?.GetValue<bool>() == true)
                {
                    byte[] bytes = buffer.ToArray();
                    Contract.Require(Contract.Number(message, "bytes") == bytes.Length &&
                        Contract.Text(message, "sha256", 64) == Convert.ToHexStringLower(SHA256.HashData(bytes)), "native_protocol", "Incomplete native probe result.");
                    return bytes;
                }
                Contract.Require(Contract.Number(message, "offset") == buffer.Length, "native_protocol", "Unexpected probe chunk offset.");
                byte[] chunk;
                try { chunk = Convert.FromBase64String(Contract.Text(message, "data", 44 * 1024)); }
                catch (FormatException) { throw new AddonException("native_protocol", "Invalid probe chunk."); }
                Contract.Require(chunk.Length is > 0 and <= 32768 && buffer.Length + chunk.Length <= (subtitles is null ? 262144 : NativeSubtitles.MaximumBytes), "native_result_too_large", "Native result exceeds its limit.");
                buffer.Write(chunk);
            }
        }
        finally
        {
            try
            {
                if (process is not null)
                {
                    job?.Terminate();
                    // Attach can fail before the gate is released. In that case
                    // the process is not owned by the job and still needs reaping.
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                    await process.WaitForExitAsync();
                    if (job is not null) await job.WaitForEmptyAsync();
                    await diagnostics;
                }
            }
            finally
            {
                process?.Dispose(); job?.Dispose();
                await MediaProcess.RemoveFilesAsync(actualRoot, directory);
            }
        }
    }
    private static async Task DrainAsync(Stream stream)
    {
        try { await stream.CopyToAsync(Stream.Null); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }
}
