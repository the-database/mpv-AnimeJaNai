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
                ["version"] = 1, ["status"] = new JsonObject { ["state"] = "failed", ["error"] = error.Message[..Math.Min(error.Message.Length, 512)] },
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
        using var player = new NativePlayback(Contract.Text(launch, "root", 4096), Contract.Text(launch, "source", 4096),
            Contract.Text(launch, "configuration", 4096), Contract.Text(launch, "work", 4096),
            checked((int)Contract.Number(launch, "slot")), Contract.Text(launch, "backend", 32), sampleHandle);
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
                            player.Pause(control["paused"]!.GetValue<bool>()); break;
                        case "seek":
                            Contract.Require(control["seconds"] is JsonValue s && s.TryGetValue<double>(out _), "invalid_request", "Invalid seek position.");
                            player.Seek(control["seconds"]!.GetValue<double>()); break;
                        default: throw new AddonException("unknown_method", "Unknown internal media control.");
                    }
                }
            }
            catch (AddonException error) when (error.Code == "worker_exited") { }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        });
        try
        {
            long last = 0;
            while (!commands.IsCompleted)
            {
                var state = player.Poll();
                if (Stopwatch.GetElapsedTime(last) >= TimeSpan.FromMilliseconds(250) || player.Ended)
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await channel.WriteAsync(new JsonObject { ["version"] = 1, ["status"] = state }, deadline.Token);
                    last = Stopwatch.GetTimestamp();
                }
                if (player.Ended) break;
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
        return player.Failed ? 1 : 0;
    }
}
