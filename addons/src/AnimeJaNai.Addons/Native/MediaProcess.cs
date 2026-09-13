using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons.Native;

// Constructed only after the trusted host resolves approved source/profile ids.
// Each instance owns its native process; a codec or driver failure does not run
// in the Manager, the service, or another session's process.
internal sealed class MediaProcess : IControllableProcessingSession, IFrameProcessingSession
{
    private readonly Process process;
    private readonly WindowsJob job;
    private readonly string directory;
    private readonly string workRoot;
    private readonly MessageChannel channel;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim commands = new(1, 1);
    private readonly object sync = new();
    private readonly Task monitoring;
    private readonly Task diagnostics;
    private JsonObject status = new() { ["state"] = "starting" };
    private Task? cleanup;
    private bool closing;
    private bool nativeReleased;
    private readonly NativeFrameBuffer? frames;

    private MediaProcess(Process process, WindowsJob job, string directory, string workRoot, NativeFrameBuffer? frames)
    {
        this.process = process; this.job = job; this.directory = directory; this.workRoot = workRoot;
        this.frames = frames;
        channel = new(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        diagnostics = DrainAsync();
        monitoring = MonitorAsync();
    }

    public static async Task<MediaProcess> StartAsync(string root, string source, string configuration, int slot, string backend,
        string workRoot, WorkerCommand command, CancellationToken token, bool enableFrameSamples = false)
    {
        token.ThrowIfCancellationRequested();
        workRoot = Path.GetFullPath(workRoot);
        string directory = SafeFiles.DirectoryPath(workRoot, "media-" + Guid.NewGuid().ToString("N"));
        WindowsJob? job = null; Process? process = null; MediaProcess? result = null;
        NativeFrameBuffer? frames = null;
        try
        {
            // Native decoders/model loading need a different budget from guest
            // logic. This does not change the Wasm worker's stricter defaults.
            job = new WindowsJob(new UIntPtr(2UL << 30), new UIntPtr(4UL << 30), cpuRate: 0);
            string snapshot = Path.Combine(directory, "animejanai.conf");
            SafeFiles.AtomicWrite(snapshot, Encoding.UTF8.GetBytes(configuration));
            var info = WorkerBridge.ProcessInfo(command.Executable, directory);
            // GPU drivers use these Windows locations for cache discovery. Keep
            // their per-process profile/cache files in this owned work tree.
            info.Environment["SystemDrive"] = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!.TrimEnd('\\');
            info.Environment["WINDIR"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            info.Environment["USERPROFILE"] = SafeFiles.DirectoryPath(directory, "profile");
            info.Environment["LOCALAPPDATA"] = SafeFiles.DirectoryPath(directory, "local");
            info.Environment["APPDATA"] = SafeFiles.DirectoryPath(directory, "roaming");
            foreach (string arg in command.PrefixArguments.Append("media-worker")) info.ArgumentList.Add(arg);
            process = Process.Start(info) ?? throw new AddonException("native_start", "Could not start native media worker.");
            job.Attach(process);
            if (enableFrameSamples && backend == "DirectML") frames = new NativeFrameBuffer();
            long sampleMapping = frames?.DuplicateTo(process) ?? 0;
            result = new(process, job, directory, workRoot, frames);
            await process.StandardInput.BaseStream.WriteAsync(new byte[] { 1 }, token);
            await result.channel.WriteAsync(new JsonObject { ["version"] = 1, ["root"] = Path.GetFullPath(root),
                ["source"] = Path.GetFullPath(source), ["configuration"] = snapshot, ["work"] = directory, ["slot"] = slot, ["backend"] = backend, ["sampleMapping"] = sampleMapping }, token);
            return result; // Native initialization progresses behind the handle.
        }
        catch (Exception error)
        {
            if (result is not null)
            {
                try { await result.DisposeAsync(); }
                catch { throw new ProcessingSessionStartException(result, error); }
            }
            else
            {
                job?.Dispose();
                if (process is not null) { try { process.Kill(true); } catch (InvalidOperationException) { } await process.WaitForExitAsync(); process.Dispose(); }
                frames?.Dispose();
                await RemoveFilesAsync(workRoot, directory);
            }
            throw;
        }
    }

    public Task<JsonObject> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync) return Task.FromResult((JsonObject)status.DeepClone());
    }
    public Task PauseAsync(bool paused, CancellationToken token) => ControlAsync(new() { ["operation"] = "pause", ["paused"] = paused }, token);
    public IFrameSubscription SubscribeFrames(FrameRequest request)
    {
        lock (sync)
        {
            Contract.Require(!closing && status["state"]?.GetValue<string>() is not ("failed" or "completed" or "closed"), "session_closed", "Native session is no longer active.");
            Contract.Require(frames is not null, "feature_unavailable", "This session does not offer frame samples. A sample-capable runtime and DirectML profile are required.");
            return frames.Subscribe(request);
        }
    }
    public Task SeekAsync(double seconds, CancellationToken token)
    {
        Contract.Require(double.IsFinite(seconds) && seconds >= 0 && seconds <= 315576000, "invalid_request", "Invalid seek position.");
        return ControlAsync(new() { ["operation"] = "seek", ["seconds"] = seconds }, token);
    }
    private async Task ControlAsync(JsonObject control, CancellationToken cancellationToken)
    {
        using var token = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        token.CancelAfter(TimeSpan.FromSeconds(1));
        await commands.WaitAsync(token.Token);
        try
        {
            lock (sync) Contract.Require(!closing && status["state"]?.GetValue<string>() is not ("failed" or "completed" or "closed"), "session_closed", "Native session is no longer active.");
            if (control["operation"]?.GetValue<string>() == "seek") frames?.InvalidateForSeek();
            await channel.WriteAsync(control, token.Token);
        }
        finally { commands.Release(); }
    }

    private async Task MonitorAsync()
    {
        try
        {
            while (true)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                var message = await channel.ReadAsync(deadline.Token);
                Contract.Require(Contract.Number(message, "version") == 1 && message["status"] is JsonObject, "native_protocol", "Invalid native media response.");
                lock (sync) status = (JsonObject)message["status"]!.DeepClone();
            }
        }
        catch (Exception error) when (error is IOException or AddonException or OperationCanceledException)
        {
            lock (sync)
            {
                if (!closing && status["state"]?.GetValue<string>() is not ("completed" or "failed"))
                    status = new() { ["state"] = "failed", ["error"] = error is OperationCanceledException ? "Native media heartbeat timed out." : error.Message[..Math.Min(error.Message.Length, 512)] };
            }
            if (!closing)
            {
                if (error is AddonException { Code: "worker_exited" })
                {
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                    catch (TimeoutException) { job.Terminate(); }
                }
                else job.Terminate();
            }
        }
    }
    private async Task DrainAsync()
    {
        try
        {
            char[] buffer = new char[1024];
            while (await process.StandardError.ReadAsync(buffer, lifetime.Token) != 0) { }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            closing = true;
            if (cleanup is null || cleanup.IsFaulted) cleanup = CloseAsync();
            return new(cleanup);
        }
    }
    private async Task CloseAsync()
    {
        await Task.Yield();
        if (!nativeReleased)
        {
            await commands.WaitAsync();
            try { process.StandardInput.Close(); }
            finally { commands.Release(); }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { job.Terminate(); }
            lifetime.Cancel(); job.Terminate();
            await job.WaitForEmptyAsync();
            await Task.WhenAll(monitoring, diagnostics);
            job.Dispose(); process.Dispose(); frames?.Dispose(); nativeReleased = true;
        }
        await RemoveFilesAsync(workRoot, directory);
        lock (sync) status = new() { ["state"] = "closed" };
    }
    private static async Task RemoveFilesAsync(string workRoot, string directory)
    {
        string resolved = Path.GetFullPath(directory);
        string name = Path.GetFileName(resolved);
        Contract.Require(Path.GetDirectoryName(resolved)!.Equals(Path.GetFullPath(workRoot), StringComparison.OrdinalIgnoreCase) &&
            name.StartsWith("media-", StringComparison.Ordinal) && Guid.TryParseExact(name[6..], "N", out _),
            "invalid_path", "Refusing to clean an unowned media work directory.");
        void RemoveTree(string path)
        {
            // Inspect each directory before descending. Do not follow junctions,
            // links or paths outside the previously verified owned work root.
            SafeFiles.CheckParents(path);
            Contract.Require(path.Equals(resolved, StringComparison.OrdinalIgnoreCase) || path.StartsWith(resolved + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "invalid_path", "Media cleanup escaped its work directory.");
            foreach (string entry in Directory.EnumerateFileSystemEntries(path))
            {
                SafeFiles.CheckParents(entry);
                if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0) RemoveTree(entry);
                else File.Delete(entry);
            }
            Directory.Delete(path, recursive: false);
        }
        for (int retry = 0; ; retry++)
        {
            try
            {
                if (Directory.Exists(resolved)) RemoveTree(resolved);
                return;
            }
            catch (IOException) when (retry < 80) { await Task.Delay(25); }
        }
    }
}
