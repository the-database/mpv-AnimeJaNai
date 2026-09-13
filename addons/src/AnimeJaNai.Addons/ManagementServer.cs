using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons;

public sealed class HostLease : IDisposable
{
    private readonly FileStream held;
    public HostLease(string root)
    {
        root = SafeFiles.DirectoryPath(root);
        string path = Path.Combine(root, "host.lock");
        SafeFiles.CheckParents(path);
        try { held = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new AddonException("host_running", "An addon host is already using this data directory. Manage it through Manager or use another development directory."); }
    }
    public void Dispose() => held.Dispose();
}

public sealed class ManagementServer(string root, AddonService service)
{
    public static string PipeName(string root) => "AJN.Addons.v1." + Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())))[..32];

    public async Task RunAsync(CancellationToken cancellationToken, bool exitWhenIdle = true)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The management service currently supports Windows only.");
        using var lease = new HostLease(root);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var slots = new SemaphoreSlim(8, 8);
        var tasks = new ConcurrentDictionary<long, Task>();
        long next = 0, lastActivity = Stopwatch.GetTimestamp();
        int connections = 0;
        NamedPipeServerStream? listener = NewPipe(first: true);
        var idle = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            try
            {
                while (await timer.WaitForNextTickAsync(lifetime.Token))
                {
                    if (Volatile.Read(ref connections) > 0 || service.HasRunningWorkers) Interlocked.Exchange(ref lastActivity, Stopwatch.GetTimestamp());
                    else if (exitWhenIdle && Stopwatch.GetElapsedTime(Interlocked.Read(ref lastActivity)) > TimeSpan.FromSeconds(30)) lifetime.Cancel();
                }
            }
            catch (OperationCanceledException) { }
        });
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await slots.WaitAsync(lifetime.Token);
                try { await listener.WaitForConnectionAsync(lifetime.Token); }
                catch { slots.Release(); throw; }
                var accepted = listener;
                try { listener = NewPipe(first: false); }
                catch { accepted.Dispose(); slots.Release(); throw; }
                Interlocked.Increment(ref connections);
                long number = Interlocked.Increment(ref next);
                var task = Task.Run(async () =>
                {
                    try { await HandleAsync(accepted, lifetime.Token); }
                    finally { Interlocked.Decrement(ref connections); Interlocked.Exchange(ref lastActivity, Stopwatch.GetTimestamp()); slots.Release(); }
                });
                tasks[number] = task;
                _ = task.ContinueWith(completed => { tasks.TryRemove(number, out _); _ = completed.Exception; }, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            lifetime.Cancel(); listener?.Dispose();
            try { await Task.WhenAll(tasks.Values.Append(idle)); }
            finally { await service.DisposeAsync(); }
        }
    }

    private NamedPipeServerStream NewPipe(bool first)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var identity = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        // Same owner rule as .NET CurrentUserOnly, plus an explicit NETWORK
        // logon denial. Supply the complete ACL at creation, with no open race.
        security.SetOwner(identity.Owner!);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new(identity.Owner!, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(PipeName(root), PipeDirection.InOut, 9, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None), 4096, 4096, security);
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using (pipe)
        {
            string client = Guid.NewGuid().ToString("N");
            var channel = new MessageChannel(pipe, pipe);
            try
            {
                while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
                {
                    var request = await channel.ReadAsync(cancellationToken);
                    JsonRpc.Validate(request);
                    var id = JsonRpc.RequestId(request);
                    string method = Contract.Text(request, "method", 64);
                    Contract.Require(request["params"] is JsonObject, "invalid_message", "Management params must be an object.");
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(30));
                    JsonObject response;
                    try { response = JsonRpc.Result(id, await service.InvokeAsync(client, method, (JsonObject)request["params"]!, deadline.Token)); }
                    catch (AddonException error) { response = JsonRpc.Error(id, error.Code, error.Message); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
                    { response = JsonRpc.Error(id, "host_error", "The host could not complete this operation: " + error.Message[..Math.Min(error.Message.Length, 1024)]); }
                    try { await channel.WriteAsync(response, cancellationToken); }
                    catch (AddonException error) when (error.Code == "message_too_large")
                    { await channel.WriteAsync(JsonRpc.Error(id, error.Code, error.Message), cancellationToken); }
                }
            }
            catch (Exception error) when (error is AddonException or IOException or OperationCanceledException) { }
            finally { await service.DisconnectAsync(client); }
        }
    }
}
