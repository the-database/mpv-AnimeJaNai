using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons;

public sealed class Broker : IAsyncDisposable
{
    private readonly AddonPackage package;
    private readonly PermissionGrant grant;
    private readonly AddonStorage storage;
    private readonly AddonSettings settings;
    private readonly SessionRegistry? sessions;
    private readonly Action<string> log;
    private readonly SessionRegistry.Owner? owner;
    private volatile bool disposed;

    public Broker(AddonPackage package, PermissionGrant grant, string dataRoot, Action<string>? log = null, SessionRegistry? sessions = null)
    {
        Contract.Require(package.Hash == grant.PackageHash, "invalid_grant", "Permissions belong to a different package.");
        this.package = package;
        this.grant = grant;
        this.log = log ?? (_ => { });
        this.sessions = sessions;
        owner = sessions?.CreateOwner(package, grant);
        foreach (var (name, requirement) in package.Manifest.RequiredCapabilities ?? [])
            Contract.Require(AvailableCapabilities().Contains(name) && requirement.Major == 1 && requirement.MinMinor <= CapabilityMinor(name),
                "missing_capability", $"Required capability is unavailable: {name}.");
        storage = new(dataRoot, package.Manifest.Id);
        settings = new(dataRoot, package.Manifest);
        _ = settings.Get();
    }

    private string[] AvailableCapabilities() => sessions is null ? ["host", "storage", "logging", "settings"] : ["host", "storage", "logging", "settings", "sessions"];
    private int CapabilityMinor(string name) => name == "sessions" && sessions is not null ? sessions.CapabilityMinor(owner!) : 0;

    public JsonObject Info() => new()
    {
        ["id"] = package.Manifest.Id,
        ["api"] = new JsonObject { ["major"] = Contract.Major, ["minor"] = Contract.Minor },
        ["permissions"] = new JsonArray(grant.Allowed.Order(StringComparer.Ordinal).Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
        ["features"] = new JsonArray(AvailableCapabilities().Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
        ["capabilities"] = new JsonObject(AvailableCapabilities().Select(p => new KeyValuePair<string, JsonNode?>(p,
            new JsonObject { ["major"] = 1, ["minor"] = CapabilityMinor(p) }))),
    };

    public async Task<JsonNode?> InvokeAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Contract.Require(!disposed, "owner_closed", "Addon instance has stopped.");
        switch (method)
        {
            case "host.info": return Info();
            case "settings.get": return settings.Get();
            case "log.write":
                grant.Demand("log.write");
                string message = Contract.Text(parameters, "message", 4096);
                log(new string(message.Where(c => !char.IsControl(c) || c == '\t').ToArray()));
                return null;
            case "storage.get":
                grant.Demand("storage.read");
                return storage.Get(Contract.Text(parameters, "key", 64));
            case "storage.set":
                grant.Demand("storage.write");
                Contract.Require(parameters.ContainsKey("value"), "invalid_request", "Missing storage value.");
                storage.Set(Contract.Text(parameters, "key", 64), parameters["value"]);
                return null;
            case "sessions.open":
                grant.Demand("sessions.manage");
                var registry = Sessions();
                string source = Contract.Text(parameters, "sourceId", 128);
                string? profile = parameters["profileId"] is null ? null : Contract.Text(parameters, "profileId", 128);
                return new JsonObject { ["sessionId"] = await registry.OpenAsync(owner!, source, profile, cancellationToken) };
            case "sessions.selections":
                grant.Demand("sessions.manage");
                return Sessions().Selections(owner!);
            case "sessions.pause":
                grant.Demand("sessions.manage");
                Contract.Require(parameters["paused"] is JsonValue paused && paused.TryGetValue<bool>(out _), "invalid_request", "Expected a pause state.");
                await Sessions().ControlAsync(owner!, Contract.Text(parameters, "sessionId", 64), parameters["paused"]!.GetValue<bool>(), null, cancellationToken);
                return null;
            case "sessions.seek":
                grant.Demand("sessions.manage");
                Contract.Require(parameters["seconds"] is JsonValue seconds && seconds.TryGetValue<double>(out _), "invalid_request", "Expected a seek position.");
                await Sessions().ControlAsync(owner!, Contract.Text(parameters, "sessionId", 64), null, parameters["seconds"]!.GetValue<double>(), cancellationToken);
                return null;
            case "sessions.status":
                grant.Demand("sessions.manage");
                return await Sessions().StatusAsync(owner!, Contract.Text(parameters, "sessionId", 64), cancellationToken);
            case "sessions.close":
                grant.Demand("sessions.manage");
                await Sessions().CloseAsync(owner!, Contract.Text(parameters, "sessionId", 64), cancellationToken);
                return null;
            case "sessions.requestClose":
                grant.Demand("sessions.manage");
                var closingRegistry = Sessions();
                string closingId = Contract.Text(parameters, "sessionId", 64);
                Contract.Require(closingRegistry.CapabilityMinor(owner!) >= 1, "feature_unavailable", "Asynchronous session close is unavailable.");
                closingRegistry.RequestClose(owner!, closingId);
                return null;
            default: throw new AddonException("unknown_method", "Method is not available in this API.");
        }
    }

    private SessionRegistry Sessions() => sessions ?? throw new AddonException("feature_unavailable", "No native processing provider is connected.");

    public async ValueTask DisposeAsync()
    {
        disposed = true;
        if (sessions is not null) await sessions.ReleaseOwnerAsync(owner!);
    }
}
