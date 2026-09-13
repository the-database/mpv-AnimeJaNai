using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons;

// Trusted management operations. This object is deliberately not reachable from
// the guest broker: only local, same-user management clients may call it.
public sealed class AddonService : IAsyncDisposable
{
    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public int Users;
        public AddonActivation? Activation;
        public AddonPackage? Package;
        public PermissionGrant? Grant;
        public IAddonInstance? Worker;
        public string? Failure;
        public readonly Queue<string> Logs = new();
        // 32 x 512 UTF-16 units fit the management response even when every
        // character needs a six-byte JSON escape.
        public void Log(string message) { lock (Logs) { if (Logs.Count == 32) Logs.Dequeue(); Logs.Enqueue(message[..Math.Min(message.Length, 512)]); } }
    }
    private readonly string root;
    private readonly AddonRegistry registry;
    private readonly MediaSelections? media;
    private readonly Func<AddonPackage, PermissionGrant, Action<string>, CancellationToken, Task<IAddonInstance>> start;
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> clients = new(StringComparer.Ordinal);
    public bool HasRunningWorkers => entries.Values.Any(e => e.Worker is { IsStopped: false });

    public AddonService(string root, Func<AddonPackage, PermissionGrant, Action<string>, CancellationToken, Task<IAddonInstance>> start, MediaSelections? media = null)
    {
        this.root = root;
        registry = new(root);
        this.start = start;
        this.media = media;
    }

    public async Task<JsonNode?> InvokeAsync(string client, string method, JsonObject parameters, CancellationToken token)
    {
        if (method == "manager.hello")
        {
            Contract.Require(Contract.Number(parameters, "major") == 1, "incompatible_api", "Manager protocol 1 is required.");
            if (clients.TryAdd(client, 0))
                foreach (string id in registry.ListIds())
                {
                    try { await WithAsync(id, async entry => { await AutoActivateAsync(entry, client, token); return null; }, token); }
                    catch (Exception error) when (error is AddonException or IOException or UnauthorizedAccessException) { }
                }
            return new JsonObject { ["major"] = 1, ["minor"] = 1, ["nativeMediaAvailable"] = media is not null };
        }
        Contract.Require(clients.ContainsKey(client), "handshake_required", "Complete manager.hello first.");
        switch (method)
        {
            case "addons.list":
                string? after = parameters["after"] is null ? null : Contract.Text(parameters, "after", 100);
                var ids = registry.ListIds().Where(id => after is null || StringComparer.Ordinal.Compare(id, after) > 0).ToArray();
                var list = new JsonArray();
                foreach (string id in ids.Take(16))
                {
                    try { list.Add(await WithAsync(id, entry => Task.FromResult<JsonNode?>(Summary(entry)), token)); }
                    catch (Exception error) when (error is AddonException or IOException or UnauthorizedAccessException)
                    { list.Add(new JsonObject { ["id"] = id, ["name"] = id, ["running"] = false, ["error"] = error.Message[..Math.Min(error.Message.Length, 256)] }); }
                }
                return new JsonObject { ["addons"] = list, ["nextCursor"] = ids.Length > 16 ? ids[15] : null };
            case "addons.inspect":
                var inspected = AddonPackage.Load(Contract.Text(parameters, "path", 4096));
                return new JsonObject { ["manifest"] = JsonSerializer.SerializeToNode(inspected.Manifest, Contract.Json), ["hash"] = inspected.Hash };
            case "addons.installDev":
                var package = AddonPackage.Load(Contract.Text(parameters, "path", 4096));
                Contract.Require(package.Hash == Contract.Text(parameters, "expectedHash", 64), "integrity_mismatch", "Package changed since it was reviewed. Inspect it again.");
                Contract.Require(parameters["permissions"] is JsonArray permissionArray && permissionArray.Count <= Contract.PermissionNames.Count, "invalid_grant", "Invalid permission grant.");
                var permissions = ((JsonArray)parameters["permissions"]!).Select(p => p is JsonValue value && value.TryGetValue<string>(out var text)
                    ? text : throw new AddonException("invalid_grant", "Invalid permission.")).ToArray();
                _ = new PermissionGrant(package, permissions);
                return await WithAsync(package.Manifest.Id, async entry =>
                {
                    await ResetAsync(entry);
                    registry.Install(package, permissions);
                    Load(entry, package.Manifest.Id);
                    foreach (string connection in clients.Keys) await AutoActivateAsync(entry, connection, token);
                    return Summary(entry);
                }, token, load: false);
            case "addons.start":
                return await WithAsync(Id(parameters), async entry =>
                {
                    if (entry.Worker?.IsStopped == true) await entry.Activation!.StopAsync();
                    await entry.Activation!.AcquireAsync("manual", "user", token); return Summary(entry);
                }, token);
            case "addons.stop":
                return await WithAsync(Id(parameters), async entry => { await entry.Activation!.StopAsync(); return Summary(entry); }, token);
            case "addons.remove":
                return await WithAsync(Id(parameters), async entry => { await ResetAsync(entry); new MediaSelections(root).Clear(Id(parameters)); registry.Disable(Id(parameters)); return null; }, token, load: false);
            case "addons.rollback":
                return await WithAsync(Id(parameters), async entry =>
                {
                    registry.ValidateRollback(Id(parameters));
                    await ResetAsync(entry); registry.Rollback(Id(parameters)); Load(entry, Id(parameters));
                    foreach (string connection in clients.Keys) await AutoActivateAsync(entry, connection, token);
                    return Summary(entry);
                }, token, load: false);
            case "addons.settings":
                return await WithAsync(Id(parameters), entry =>
                {
                    var editable = new AddonSettings(root, entry.Package!.Manifest).GetForEditing();
                    return Task.FromResult<JsonNode?>(new JsonObject
                    {
                        ["definitions"] = JsonSerializer.SerializeToNode(entry.Package.Manifest.Settings, Contract.Json),
                        ["values"] = editable.Values,
                        ["invalidSettings"] = JsonSerializer.SerializeToNode(editable.Invalid, Contract.Json),
                        ["actions"] = JsonSerializer.SerializeToNode(entry.Package.Manifest.Actions, Contract.Json),
                    });
                }, token);
            case "addons.configure":
                Contract.Require(parameters["changes"] is JsonObject, "invalid_settings", "Expected a settings patch.");
                return await WithAsync(Id(parameters), async entry => await entry.Activation!.UpdateSettingsAsync(
                    new(root, entry.Package!.Manifest), (JsonObject)parameters["changes"]!, token), token);
            case "addons.action":
                return await WithAsync(Id(parameters), entry => entry.Activation!.RunActionAsync(Contract.Text(parameters, "action", 64), token), token);
            case "addons.logs":
                return await WithAsync(Id(parameters), entry =>
                {
                    lock (entry.Logs) return Task.FromResult<JsonNode?>(new JsonArray(entry.Logs.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray()));
                }, token, load: false);
            case "media.selections":
                return await WithAsync(Id(parameters), entry => Task.FromResult<JsonNode?>(Media().List(entry.Package!, entry.Grant!, includePaths: true)), token);
            case "media.approveSource":
                return await WithAsync(Id(parameters), entry =>
                {
                    DemandReviewed(entry, parameters);
                    return Task.FromResult<JsonNode?>(new JsonObject { ["sourceId"] = Media().ApproveSource(entry.Package!, entry.Grant!, Contract.Text(parameters, "path", 1024)) });
                }, token);
            case "media.approveProfile":
                return await WithAsync(Id(parameters), entry =>
                {
                    DemandReviewed(entry, parameters);
                    long slot = Contract.Number(parameters, "slot");
                    Contract.Require(slot is >= 1 and <= 1013, "invalid_profile", "Invalid slot.");
                    return Task.FromResult<JsonNode?>(new JsonObject { ["profileId"] = Media().ApproveProfile(entry.Package!, entry.Grant!,
                        Contract.Text(parameters, "name", 128), (int)slot, Contract.Text(parameters, "backend", 32), Contract.Text(parameters, "configuration", 50000)) });
                }, token);
            case "media.revoke":
                return await WithAsync(Id(parameters), async entry =>
                {
                    DemandReviewed(entry, parameters);
                    var selectedMedia = Media();
                    string kind = Contract.Text(parameters, "kind", 16), selectedId = Contract.Text(parameters, "selectionId", 64);
                    Contract.Require(kind is "source" or "profile", "invalid_request", "Unknown media approval kind.");
                    await entry.Activation!.StopAsync();
                    selectedMedia.Revoke(entry.Package!, entry.Grant!, kind, selectedId);
                    return null;
                }, token);
            default: throw new AddonException("unknown_method", "Unknown management operation.");
        }
    }

    private static string Id(JsonObject parameters) => Contract.Text(parameters, "id", 100);
    private MediaSelections Media() => media ?? throw new AddonException("feature_unavailable", "This host does not include native media sessions.");
    private static void DemandReviewed(Entry entry, JsonObject parameters) => Contract.Require(
        entry.Package!.Hash == Contract.Text(parameters, "expectedHash", 64), "integrity_mismatch", "Addon changed since resource consent was reviewed. Refresh and review it again.");

    private async Task<JsonNode?> WithAsync(string id, Func<Entry, Task<JsonNode?>> action, CancellationToken token, bool load = true)
    {
        Contract.Require(Contract.ValidId(id), "invalid_id", "Invalid addon id.");
        Entry entry;
        lock (entries)
        {
            Contract.Require(entries.ContainsKey(id) || entries.Count < 128, "capacity_exceeded", "Too many addons in this host.");
            entry = entries.GetOrAdd(id, _ => new());
            entry.Users++;
        }
        bool held = false;
        try
        {
            await entry.Gate.WaitAsync(token); held = true;
            if (load && entry.Activation is null) Load(entry, id);
            var result = await action(entry);
            return result;
        }
        catch (Exception error) { if (held) entry.Failure = error.Message[..Math.Min(error.Message.Length, 1024)]; throw; }
        finally
        {
            if (held) entry.Gate.Release();
            lock (entries)
            {
                // Failed lookups and removed addons must not consume one of the
                // 128 slots forever. Waiters keep the entry alive until they finish.
                if (--entry.Users == 0 && entry.Activation is null) entries.TryRemove(id, out _);
            }
        }
    }

    private void Load(Entry entry, string id)
    {
        var (package, grant) = registry.Load(id);
        entry.Package = package;
        entry.Grant = grant;
        entry.Activation = new(package, async token =>
        {
            entry.Failure = null;
            var worker = await start(package, grant, entry.Log, token);
            entry.Worker = worker;
            return worker;
        });
    }

    private static JsonObject Summary(Entry entry) => new()
    {
        ["id"] = entry.Package!.Manifest.Id, ["name"] = entry.Package.Manifest.Name, ["version"] = entry.Package.Manifest.Version,
        ["hash"] = entry.Package.Hash, ["running"] = entry.Worker is { IsStopped: false }, ["error"] = entry.Failure ?? (entry.Worker as AddonWorker)?.Failure,
        ["manual"] = (entry.Package.Manifest.Activation ?? ["manual"]).Contains("manual", StringComparer.Ordinal),
        ["mediaPermission"] = entry.Grant!.Allowed.Contains("sessions.manage", StringComparer.Ordinal),
    };

    private static async Task AutoActivateAsync(Entry entry, string client, CancellationToken token)
    {
        if (entry.Package!.Manifest.Activation?.Contains("on_manager", StringComparer.Ordinal) != true) return;
        try { await entry.Activation!.AcquireAsync("on_manager", client, token); }
        catch (AddonException error) { entry.Failure = error.Message; }
    }

    public async Task DisconnectAsync(string client)
    {
        clients.TryRemove(client, out _);
        foreach (var entry in entries.Values)
        {
            await entry.Gate.WaitAsync();
            try { if (entry.Activation is not null) await entry.Activation.ReleaseAsync("on_manager", client); }
            catch (Exception error) { entry.Failure = error.Message[..Math.Min(error.Message.Length, 1024)]; }
            finally { entry.Gate.Release(); }
        }
    }

    private static async Task ResetAsync(Entry entry)
    {
        if (entry.Activation is not null) await entry.Activation.DisposeAsync();
        entry.Activation = null; entry.Worker = null; entry.Package = null; entry.Grant = null; entry.Failure = null;
    }

    public async ValueTask DisposeAsync()
    {
        List<Exception> errors = [];
        foreach (var entry in entries.Values)
        {
            await entry.Gate.WaitAsync();
            try { await ResetAsync(entry); }
            catch (Exception error) { errors.Add(error); }
            finally { entry.Gate.Release(); }
        }
        if (errors.Count > 0) throw new AggregateException("Some addons could not be cleaned up.", errors);
    }
}
