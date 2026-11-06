using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons;

public sealed record RemoteInputRequest(string DestinationId, string Path, bool UseCredential = false, string? CredentialId = null)
{
    public void Validate() => Contract.Require(DestinationId is { Length: > 0 and <= 64 } && Path is { Length: > 0 and <= 2048 } &&
        (CredentialId is null || CredentialId.Length is > 0 and <= 64 && !UseCredential),
        "invalid_input", "Choose an approved service and a relative media path.");
    public static RemoteInputRequest Parse(JsonObject value)
    {
        Contract.Require(Contract.Text(value, "type", 32) == "http", "feature_unavailable", "This provider supports approved HTTP media sources.");
        Contract.Require(value["useCredential"] is JsonValue credential && credential.TryGetValue<bool>(out _),
            "invalid_input", "Choose whether to use a saved credential.");
        var result = new RemoteInputRequest(Contract.Text(value, "destinationId", 64), Contract.Text(value, "path", 2048),
            value["useCredential"]!.GetValue<bool>(), value["credentialId"] is null ? null : Contract.Text(value, "credentialId", 64));
        result.Validate(); return result;
    }
}

// Private supervisor-to-media-worker data. Never send this object to a guest,
// put it on the command line, persist it, or include it in diagnostics.
internal sealed class RemoteInputPlan(NetworkDestination destination, string path, string? header = null, string? credential = null,
    string? effectivePath = null, Dictionary<string, string[]>? delegatedHeaders = null)
{
    internal NetworkDestination Destination { get; } = destination;
    internal Uri Target { get; } = NetworkAccess.RequestTarget(destination, effectivePath ?? path, effectivePath is null ? 2048 : 65536);
    internal string IdentityKey => Destination.Origin + path;
    internal string? Header { get; } = header;
    internal string? Credential { get; } = credential;
    internal Dictionary<string, string[]> DelegatedHeaders { get; } = delegatedHeaders ?? new(StringComparer.OrdinalIgnoreCase);
    public override string ToString() => "Private remote media plan";

    internal void Validate()
    {
        NetworkSelections.ValidateDestination(Destination);
        Contract.Require(Destination.Scheme is "http" or "https", "invalid_input", "Remote media requires an approved HTTP service.");
        Contract.Require((Header is null) == (Credential is null), "invalid_credential", "Invalid remote media credential.");
        if (Header is not null)
        {
            NetworkAccess.ValidateHeader(Header, Credential!);
            Contract.Require(Header.ToLowerInvariant() is not ("range" or "if-range" or "accept-encoding"),
                "invalid_credential", "AJN manages range and content encoding headers for media sources.");
            using var message = new HttpRequestMessage();
            Contract.Require(message.Headers.TryAddWithoutValidation(Header, Credential), "invalid_credential", "Use a request header for the media credential.");
        }
        Contract.Require(DelegatedHeaders.Count <= 8 && DelegatedHeaders.All(h => h.Value is not null && h.Value.All(v => v is not null)) &&
            DelegatedHeaders.Sum(h => h.Value.Sum(v => h.Key.Length + v.Length)) <= 32768,
            "invalid_credential", "Invalid delegated media headers.");
        foreach (var (name, values) in DelegatedHeaders)
        {
            Contract.Require(values.Length is > 0 and <= 8 && name.ToLowerInvariant() is not ("range" or "if-range" or "accept-encoding") &&
                !name.Equals(Header, StringComparison.OrdinalIgnoreCase), "invalid_credential", "A media header has conflicting owners.");
            foreach (string value in values) NetworkAccess.ValidateHeader(name, value);
            using var message = new HttpRequestMessage();
            Contract.Require(message.Headers.TryAddWithoutValidation(name, values), "invalid_credential", "Use request headers for media credentials.");
        }
    }

    internal static RemoteInputPlan Prepare(AddonPackage package, PermissionGrant grant, NetworkSelections selections, RemoteInputRequest request, RequestCredentials.Lease? delegated = null)
    {
        grant.Demand("media.input"); grant.Demand("sessions.manage"); grant.Demand("network.connect");
        request.Validate();
        var selected = selections.Resolve(package, grant, request.DestinationId);
        Contract.Require((request.CredentialId is null) == (delegated is null), "credential_not_found", "This media request needs its scoped credential context.");
        string? credential = request.UseCredential ? selections.Credential(package, grant, selected) : null;
        using var message = new HttpRequestMessage(HttpMethod.Get, NetworkAccess.RequestTarget(selected.Destination, request.Path));
        delegated?.Apply(message);
        var plan = new RemoteInputPlan(selected.Destination with { Addresses = selected.Destination.Addresses.ToArray() },
            request.Path, credential is null ? null : selected.CredentialHeader, credential,
            delegated is null ? null : message.RequestUri!.PathAndQuery, message.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray(), StringComparer.OrdinalIgnoreCase));
        plan.Validate(); return plan;
    }

    internal JsonObject ToPrivateJson() => new()
    {
        ["destination"] = JsonSerializer.SerializeToNode(Destination, Contract.Json),
        ["path"] = path, ["header"] = Header, ["credential"] = Credential,
        ["effectivePath"] = Target.PathAndQuery, ["delegatedHeaders"] = JsonSerializer.SerializeToNode(DelegatedHeaders, Contract.Json),
    };

    internal static RemoteInputPlan FromPrivateJson(JsonObject value)
    {
        Contract.Require(value["destination"] is JsonObject, "invalid_input", "Missing resolved media destination.");
        NetworkDestination? destination;
        try { destination = value["destination"]!.Deserialize<NetworkDestination>(Contract.Json); }
        catch (JsonException) { throw new AddonException("invalid_input", "Invalid resolved media destination."); }
        Contract.Require(destination is not null, "invalid_input", "Missing resolved media destination.");
        var plan = new RemoteInputPlan(destination, Contract.Text(value, "path", 2048),
            value["header"] is null ? null : Contract.Text(value, "header", 64),
            value["credential"] is null ? null : Contract.Text(value, "credential", 4096),
            value["effectivePath"] is null ? null : Contract.Text(value, "effectivePath", 65536),
            value["delegatedHeaders"]?.Deserialize<Dictionary<string, string[]>>(Contract.Json));
        plan.Validate(); return plan;
    }
}
