using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons;

public sealed record ProbeRequest(string? SourceId, RemoteInputRequest? Remote)
{
    public static ProbeRequest Parse(JsonObject source)
    {
        string type = Contract.Text(source, "type", 16);
        return type switch
        {
            "local" => new(Contract.Text(source, "sourceId", 128), null),
            "http" => new(null, RemoteInputRequest.Parse(source)),
            _ => throw new AddonException("invalid_source", "Choose an approved local or HTTP source.")
        };
    }
}
public interface IMediaProbeProvider
{
    bool SupportsProbes { get; }
    Task<JsonObject> ProbeAsync(ProbeRequest request, CancellationToken token);
}

public sealed class MediaProbeAccess(PermissionGrant grant, IMediaProbeProvider provider) : IAsyncDisposable
{
    private static readonly SemaphoreSlim HostSlots = new(8, 8);
    private readonly object sync = new();
    private readonly Dictionary<string, Job> jobs = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;
    private sealed class Job(CancellationToken token)
    {
        public readonly CancellationTokenSource Stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        public Task Work = Task.CompletedTask;
        public byte[]? Result;
        public string? Error;
    }
    public JsonObject Formats()
    {
        Demand(); return new() { ["maximumJobs"] = 2, ["maximumSeconds"] = 30, ["maximumResultBytes"] = 262144,
            ["maximumChunkBytes"] = 32768, ["startsEncoder"] = false };
    }
    public string Open(ProbeRequest source)
    {
        lock (sync)
        {
            Demand(); grant.Demand("media.input");
            Contract.Require((source.SourceId is null) != (source.Remote is null), "invalid_source", "Choose one approved probe source.");
            if (source.Remote is not null) grant.Demand("network.connect");
            Contract.Require(jobs.Count < 2, "capacity_exceeded", "Close completed probes before starting another. Maximum: two.");
            Contract.Require(HostSlots.Wait(0), "capacity_exceeded", "Native probe capacity is in use.");
            string id = Guid.NewGuid().ToString("N"); var job = new Job(lifetime.Token); jobs.Add(id, job);
            job.Stop.CancelAfter(TimeSpan.FromSeconds(30));
            job.Work = Task.Run(async () =>
            {
                try
                {
                    JsonObject result = await provider.ProbeAsync(source, job.Stop.Token);
                    job.Stop.Token.ThrowIfCancellationRequested();
                    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(result, Contract.Json);
                    Contract.Require(bytes.Length <= 262144, "probe_result_too_large", "Probe result exceeds 256 KiB.");
                    lock (sync) job.Result = bytes;
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                { lock (sync) job.Error = e is AddonException a ? a.Code : e is OperationCanceledException ? "probe_cancelled" : "probe_failed"; }
                finally { HostSlots.Release(); }
            });
            return id;
        }
    }
    public JsonObject Status(string id)
    {
        lock (sync)
        {
            var job = Find(id);
            return new() { ["probeId"] = id, ["state"] = !job.Work.IsCompleted ? "pending" : job.Error is null ? "completed" : "failed",
                ["byteLength"] = job.Work.IsCompleted ? job.Result?.Length : null,
                ["error"] = job.Error is null ? null : new JsonObject { ["code"] = job.Error, ["message"] = "The approved source could not be probed." } };
        }
    }
    public BrokerResponse Result(string id, int offset, int count)
    {
        Contract.Require(offset is >= 0 and <= 262144 && count is >= 1 and <= 32768, "invalid_request", "Invalid probe chunk offset or size.");
        lock (sync)
        {
            var job = Find(id);
            Contract.Require(job.Work.IsCompleted && job.Result is not null, "probe_not_ready", "Wait for a successful completed probe.");
            Contract.Require(offset <= job.Result.Length, "invalid_request", "Offset exceeds probe result length.");
            int length = Math.Min(count, job.Result.Length - offset);
            return new(new JsonObject { ["byteLength"] = length, ["offset"] = offset, ["totalBytes"] = job.Result.Length,
                ["eof"] = offset + length == job.Result.Length }, job.Result.AsMemory(offset, length));
        }
    }
    public void Cancel(string id) { lock (sync) Find(id).Stop.Cancel(); }
    public void Close(string id)
    {
        lock (sync)
        {
            var job = Find(id); Contract.Require(job.Work.IsCompleted, "probe_active", "Cancel and wait for completion before closing the probe.");
            jobs.Remove(id); job.Stop.Dispose();
        }
    }
    private Job Find(string id)
    {
        Demand(); Contract.Require(jobs.TryGetValue(id, out var job), "probe_not_found", "Probe was closed or belongs to another addon instance."); return job;
    }
    private void Demand()
    {
        grant.Demand("sessions.manage");
        Contract.Require(provider.SupportsProbes, "feature_unavailable", "The matching native probe runtime is not installed.");
        Contract.Require(!disposed, "owner_closed", "Addon has stopped.");
    }
    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        lock (sync) { disposed = true; lifetime.Cancel(); tasks = jobs.Values.Select(j => j.Work).ToArray(); }
        await Task.WhenAll(tasks);
        lock (sync) { foreach (var job in jobs.Values) job.Stop.Dispose(); jobs.Clear(); }
    }
}
