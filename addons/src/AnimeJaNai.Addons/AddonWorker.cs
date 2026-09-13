using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text;

namespace AnimeJaNai.Addons;

public sealed class AddonWorker : IAddonInstance
{
    private static readonly SemaphoreSlim WorkerSlots = new(8, 8);
    private readonly Process process;
    private readonly WindowsJob job;
    private readonly Broker broker;
    private readonly string directory;
    private readonly MessageChannel channel;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Task stderr;
    private readonly TimeSpan eventTimeout;
    private readonly CancellationTokenSource lifetime = new();
    private Task heartbeat = Task.CompletedTask;
    private readonly StringBuilder diagnostics = new();
    private long eventId;
    private volatile bool stopped;
    private readonly object stateLock = new();
    private Task? cleanup;
    private bool processReleased;
    private string? failure;
    private int recentCalls;
    private long rateWindow = Stopwatch.GetTimestamp();
    private int binaryBytes;
    private long binaryWindow = Stopwatch.GetTimestamp();

    private AddonWorker(Process process, WindowsJob job, Broker broker, string directory, TimeSpan eventTimeout)
    {
        this.process = process; this.job = job; this.broker = broker; this.directory = directory; this.eventTimeout = eventTimeout;
        channel = new(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        stderr = DrainErrorsAsync();
    }

    public static async Task<AddonWorker> StartAsync(AddonPackage package, PermissionGrant grant, string runtime,
        string workRoot, string dataRoot, WorkerCommand command, Action<string>? log = null,
        SessionRegistry? sessions = null, TimeSpan? eventTimeout = null, CancellationToken cancellationToken = default, NetworkSelections? networkSelections = null, PlayerFrameRegistry? playerFrames = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This host currently enforces worker resource limits on Windows only.");
        WorkerBridge.VerifyRuntime(runtime);
        Contract.Require(grant.PackageHash == package.Hash, "invalid_grant", "Permissions belong to another package.");
        Contract.Require(await WorkerSlots.WaitAsync(0, cancellationToken), "capacity_exceeded", "This host already has eight addon workers.");
        string? directory = null;
        Broker? broker = null;
        WindowsJob? job = null;
        Process? process = null;
        AddonWorker? worker = null;
        try
        {
            broker = new Broker(package, grant, dataRoot, log, sessions, networkSelections, playerFrames);
            job = new WindowsJob();
            directory = SafeFiles.DirectoryPath(workRoot, "worker-" + Guid.NewGuid().ToString("N"));
            string module = Path.Combine(directory, "module.wasm");
            package.WriteModule(module);
            var info = WorkerBridge.ProcessInfo(command.Executable, directory);
            foreach (string arg in command.PrefixArguments.Concat(new[] { "worker", Path.GetFullPath(runtime), module }))
                info.ArgumentList.Add(arg);
            process = Process.Start(info) ?? throw new AddonException("worker_start", "Could not start addon worker.");
            job.Attach(process);
            worker = new(process, job, broker, directory, eventTimeout ?? TimeSpan.FromSeconds(2));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            await process.StandardInput.BaseStream.WriteAsync(new byte[] { 1 }, deadline.Token);
            await process.StandardInput.BaseStream.FlushAsync(deadline.Token);
            var hello = await worker.channel.ReadAsync(deadline.Token);
            JsonRpc.Validate(hello);
            var helloId = JsonRpc.RequestId(hello);
            Contract.Require(Contract.Text(hello, "method") == "addon.hello" && hello["params"] is JsonObject helloParams &&
                Contract.Number(helloParams, "major") == Contract.Major && Contract.Number(helloParams, "minMinor") is >= 0 and <= Contract.Minor,
                "incompatible_protocol", "Addon did not complete the protocol handshake.");
            await worker.channel.WriteAsync(JsonRpc.Result(helloId, broker.Info()), deadline.Token);
            Contract.Require(!worker.stopped, "worker_stopped", "Addon stopped during startup.");
            worker.heartbeat = worker.HeartbeatAsync();
            return worker;
        }
        catch
        {
            if (worker is not null) await worker.DisposeAsync();
            else
            {
                try
                {
                    job?.Dispose();
                    if (process is not null) { try { process.Kill(true); } catch (InvalidOperationException) { } process.Dispose(); }
                    if (broker is not null) await broker.DisposeAsync();
                }
                finally { try { if (directory is not null) await CleanupDirectoryAsync(directory); } finally { WorkerSlots.Release(); } }
            }
            throw;
        }
    }

    public Task<JsonNode?> SendEventAsync(string name, JsonNode? data = null, CancellationToken cancellationToken = default)
        => SendEventCoreAsync(name, data, cancellationToken);

    private async Task<JsonNode?> SendEventCoreAsync(string name, JsonNode? data, CancellationToken cancellationToken, Func<JsonNode?>? prepare = null)
    {
        Contract.Require(Contract.ValidKey(name), "invalid_event", "Invalid event name.");
        Contract.Require(!stopped, "worker_stopped", failure ?? "Addon worker has stopped.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await gate.WaitAsync(linked.Token);
        try
        {
            Contract.Require(!stopped, "worker_stopped", failure ?? "Addon worker has stopped.");
            if (prepare is not null)
            {
                data = prepare();
                if (data is null) return null;
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            deadline.CancelAfter(eventTimeout);
            var token = deadline.Token;
            long current = ++eventId;
            string responseId = "host:" + current;
            await channel.WriteAsync(JsonRpc.Request(JsonValue.Create(responseId), "addon.event",
                new JsonObject { ["type"] = "event", ["eventId"] = current, ["name"] = name, ["data"] = data?.DeepClone() }), token);
            var requestIds = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                var message = await channel.ReadAsync(token);
                JsonRpc.Validate(message);
                if (!message.ContainsKey("method"))
                {
                    Contract.Require(Contract.Text(message, "id") == responseId && (message.ContainsKey("result") ^ message.ContainsKey("error")),
                        "invalid_message", "Mismatched event response.");
                    if (message["error"] is JsonObject error)
                    {
                        _ = Contract.Number(error, "code");
                        throw new AddonException("addon_fault", Contract.Text(error, "message", 4096));
                    }
                    Contract.Require(!message.ContainsKey("error"), "invalid_message", "Invalid event error.");
                    return message["result"]?.DeepClone();
                }
                Contract.Require(requestIds.Count < 128, "request_quota", "Too many requests in one event.");
                var id = JsonRpc.RequestId(message);
                Contract.Require(requestIds.Add(id.ToJsonString()), "invalid_message", "Duplicate request id.");
                if (Stopwatch.GetElapsedTime(rateWindow) >= TimeSpan.FromSeconds(1)) { rateWindow = Stopwatch.GetTimestamp(); recentCalls = 0; }
                Contract.Require(++recentCalls <= 500, "request_quota", "Addon request rate exceeded.");
                string method = Contract.Text(message, "method", 64);
                Contract.Require(message["params"] is JsonObject, "invalid_message", "Request params must be an object.");
                JsonObject response;
                ReadOnlyMemory<byte> binary = default;
                try
                {
                    if (Stopwatch.GetElapsedTime(binaryWindow) >= TimeSpan.FromSeconds(1)) { binaryWindow = Stopwatch.GetTimestamp(); binaryBytes = 0; }
                    // HTTP results are consumed once. Reserve their maximum
                    // before consuming, so sample traffic cannot discard one.
                    if (method == "network.result") Contract.Require(binaryBytes + NetworkAccess.MaxResponseBytes <= 16 * 1024 * 1024,
                        "bandwidth_exceeded", "Binary response budget is full. Read the network result after reducing sample traffic.");
                    var result = await Task.Run(() => broker.InvokeTransportAsync(method, (JsonObject)message["params"]!, token), token).WaitAsync(token);
                    Contract.Require(result.Binary.Length <= FrameRequest.MaxBytes && binaryBytes + result.Binary.Length <= 16 * 1024 * 1024,
                        "bandwidth_exceeded", "Addon binary responses exceeded 16 MiB in this one-second window. Reduce the size or rate.");
                    binaryBytes += result.Binary.Length;
                    response = JsonRpc.Result(id, result.Result);
                    binary = result.Binary;
                }
                catch (AddonException error) { response = JsonRpc.Error(id, error.Code, error.Message); }
                catch (IOException) { response = JsonRpc.Error(id, "storage_unavailable", "Addon storage could not be accessed."); }
                await channel.WriteBinaryAsync(response, binary, token);
            }
        }
        catch (OperationCanceledException)
        {
            Stop("Addon event exceeded its deadline or was cancelled.");
            throw new AddonException("event_timeout", "Addon event exceeded its deadline or was cancelled.");
        }
        catch { Stop("Addon failed while handling an event."); throw; }
        finally { gate.Release(); }
    }

    private async Task HeartbeatAsync()
    {
        try
        {
            long lastPing = Stopwatch.GetTimestamp(), lastTimer = 0;
            while (!lifetime.IsCancellationRequested)
            {
                if (Stopwatch.GetElapsedTime(lastPing) >= TimeSpan.FromSeconds(5))
                {
                    await SendEventAsync("host.ping", cancellationToken: lifetime.Token);
                    lastPing = Stopwatch.GetTimestamp();
                }
                if (broker.Timers.Due() is { } ticket)
                {
                    // Bound all background timer callbacks together to 60 Hz.
                    // The event gate keeps action/settings callbacks serialized.
                    var remaining = TimeSpan.FromSeconds(1.0 / 60) - Stopwatch.GetElapsedTime(lastTimer);
                    if (remaining > TimeSpan.Zero) await Task.Delay(remaining, lifetime.Token);
                    await SendEventCoreAsync("timer", null, lifetime.Token, () => broker.Timers.Take(ticket));
                    lastTimer = Stopwatch.GetTimestamp();
                }
                else
                {
                    var untilPing = TimeSpan.FromSeconds(5) - Stopwatch.GetElapsedTime(lastPing);
                    await broker.Timers.WaitAsync(untilPing > TimeSpan.Zero ? untilPing : TimeSpan.Zero, lifetime.Token);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { Stop("Addon failed its health check."); }
    }

    public string Diagnostics { get { lock (diagnostics) return diagnostics.ToString(); } }
    public bool IsStopped => stopped;
    public string? Failure => stopped && failure != "Addon worker was closed." ? failure : null;

    private async Task DrainErrorsAsync()
    {
        byte[] buffer = new byte[4096];
        int total = 0, n;
        try
        {
            while ((n = await process.StandardError.BaseStream.ReadAsync(buffer)) > 0)
            {
                total += n;
                lock (diagnostics)
                    if (diagnostics.Length < 8192)
                        diagnostics.Append(new string(Encoding.UTF8.GetString(buffer, 0, Math.Min(n, 8192 - diagnostics.Length))
                            .Where(c => !char.IsControl(c) || c is '\n' or '\t').ToArray()));
                if (total > 64 * 1024) { Stop("Addon diagnostic output exceeded its limit."); return; }
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private void Stop(string reason)
    {
        lock (stateLock)
        {
            if (stopped) return;
            failure = reason;
            stopped = true;
            lifetime.Cancel();
            job.Terminate();
            // Cleanup runs on failure even if the embedding caller forgets Dispose.
            // Schedule it separately so it can await the heartbeat/stderr tasks.
            cleanup = Task.Run(CleanupAsync);
            _ = cleanup.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    public ValueTask DisposeAsync()
    {
        Stop("Addon worker was closed.");
        lock (stateLock)
        {
            if (cleanup!.IsFaulted) cleanup = Task.Run(CleanupAsync);
            return new(cleanup);
        }
    }

    private async Task CleanupAsync()
    {
        List<Exception> errors = [];
        if (!processReleased)
        {
            try { await Task.WhenAll(process.WaitForExitAsync(), job.WaitForEmptyAsync(), stderr, heartbeat).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception error) { errors.Add(error); }
            finally
            {
                process.Dispose(); job.Dispose(); processReleased = true; WorkerSlots.Release();
            }
        }
        try { await broker.DisposeAsync(); } catch (Exception error) { errors.Add(error); }
        try { await CleanupDirectoryAsync(directory); } catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0) throw new AggregateException("Addon resource cleanup failed; retry stopping the addon.", errors);
    }

    private static async Task CleanupDirectoryAsync(string directory)
    {
        // Remove only the file created by this host. Never recursively delete a
        // user path or follow a link if the workspace changed during execution.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                SafeFiles.CheckParents(directory);
                if (!Directory.Exists(directory)) return;
                File.Delete(Path.Combine(directory, "module.wasm"));
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
                return;
            }
            catch (IOException error) when (attempt < 80 && (error.HResult & 0xFFFF) is 32 or 33)
            {
                // Windows may finish releasing a terminated process's current
                // directory just after the job reports zero active processes.
                await Task.Delay(25);
            }
        }
    }
}
