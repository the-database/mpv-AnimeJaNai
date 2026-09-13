using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons;

public interface IAddonInstance : IAsyncDisposable
{
    bool IsStopped { get; }
    Task<JsonNode?> SendEventAsync(string name, JsonNode? data = null, CancellationToken cancellationToken = default);
}

// One controller per installed addon in the trusted host. Activation keys are
// owned by the host (manager window/player/login), never supplied by a worker.
public sealed class AddonActivation : IAsyncDisposable
{
    private readonly AddonManifest manifest;
    private readonly Func<CancellationToken, Task<IAddonInstance>> factory;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HashSet<(string Kind, string Key)> reasons = [];
    private IAddonInstance? instance;
    private bool disposed;

    public AddonActivation(AddonPackage package, Func<CancellationToken, Task<IAddonInstance>> factory)
    {
        manifest = package.Manifest;
        this.factory = factory;
    }

    public async Task AcquireAsync(string kind, string key, CancellationToken cancellationToken = default)
    {
        Contract.Require((manifest.Activation ?? ["manual"]).Contains(kind, StringComparer.Ordinal), "activation_denied", "Activation trigger was not declared.");
        ValidateKey(key);
        await gate.WaitAsync(cancellationToken);
        try
        {
            Contract.Require(!disposed, "owner_closed", "Addon controller has stopped.");
            if (instance?.IsStopped == true) throw new AddonException("addon_fault", "Addon stopped. Restart it explicitly to retry.");
            if (reasons.Contains((kind, key))) return;
            Contract.Require(reasons.Count < 64, "capacity_exceeded", "Too many activation sources.");
            if (instance is null) await StartAsync(kind, cancellationToken);
            reasons.Add((kind, key));
        }
        finally { gate.Release(); }
    }

    public async Task ReleaseAsync(string kind, string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (reasons.Remove((kind, key)) && reasons.Count == 0) await StopInstanceAsync("idle");
        }
        finally { gate.Release(); }
    }

    public async Task<JsonNode?> RunActionAsync(string id, CancellationToken cancellationToken = default)
    {
        Contract.Require(manifest.Actions?.ContainsKey(id) == true, "unknown_action", "Action was not declared by this addon.");
        await gate.WaitAsync(cancellationToken);
        try
        {
            Contract.Require(!disposed, "owner_closed", "Addon controller has stopped.");
            bool temporary = instance is null;
            if (temporary) await StartAsync("action", cancellationToken);
            try { return await instance!.SendEventAsync("action", new JsonObject { ["id"] = id }, cancellationToken); }
            finally { if (temporary) await StopInstanceAsync("action_complete"); }
        }
        finally { gate.Release(); }
    }

    public async Task<JsonObject> UpdateSettingsAsync(AddonSettings settings, JsonObject changes, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            Contract.Require(!disposed, "owner_closed", "Addon controller has stopped.");
            var updated = settings.Update(changes);
            // Saving is durable even if a running addon rejects the change event.
            if (instance is { IsStopped: false }) await instance.SendEventAsync("settings.changed", updated, cancellationToken);
            return updated;
        }
        finally { gate.Release(); }
    }

    public async Task StopAsync()
    {
        await gate.WaitAsync();
        try { reasons.Clear(); await StopInstanceAsync("disabled"); }
        finally { gate.Release(); }
    }

    private async Task StartAsync(string reason, CancellationToken cancellationToken)
    {
        var created = await factory(cancellationToken);
        try
        {
            await created.SendEventAsync("start", new JsonObject { ["reason"] = reason }, cancellationToken);
            instance = created;
        }
        catch { await created.DisposeAsync(); throw; }
    }

    private async Task StopInstanceAsync(string reason)
    {
        var closing = instance;
        instance = null;
        if (closing is null) return;
        try
        {
            if (!closing.IsStopped)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await closing.SendEventAsync("stop", new JsonObject { ["reason"] = reason }, deadline.Token);
            }
        }
        catch (Exception error) when (error is AddonException or OperationCanceledException or IOException) { }
        finally { await closing.DisposeAsync(); }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { disposed = true; reasons.Clear(); await StopInstanceAsync("host_closed"); }
        finally { gate.Release(); }
    }

    private static void ValidateKey(string key) => Contract.Require(key is { Length: > 0 and <= 128 }, "invalid_activation", "Invalid activation key.");
}
