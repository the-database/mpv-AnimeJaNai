using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons;

public sealed record RemoteInputRequest(string DestinationId, string Path, bool UseCredential = false)
{
    public void Validate() => Contract.Require(DestinationId is { Length: > 0 and <= 64 } && Path is { Length: > 0 and <= 2048 },
        "invalid_input", "Choose an approved service and a relative media path.");
    public static RemoteInputRequest Parse(JsonObject value)
    {
        Contract.Require(Contract.Text(value, "type", 32) == "http", "feature_unavailable", "This provider supports approved HTTP media sources.");
        Contract.Require(value["useCredential"] is JsonValue credential && credential.TryGetValue<bool>(out _),
            "invalid_input", "Choose whether to use a saved credential.");
        var result = new RemoteInputRequest(Contract.Text(value, "destinationId", 64), Contract.Text(value, "path", 2048),
            value["useCredential"]!.GetValue<bool>());
        result.Validate(); return result;
    }
}

// Private supervisor-to-media-worker data. Never send this object to a guest,
// put it on the command line, persist it, or include it in diagnostics.
internal sealed class RemoteInputPlan(NetworkDestination destination, string path, string? header = null, string? credential = null)
{
    internal NetworkDestination Destination { get; } = destination;
    internal Uri Target { get; } = NetworkAccess.RequestTarget(destination, path);
    internal string? Header { get; } = header;
    internal string? Credential { get; } = credential;
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
    }

    internal static RemoteInputPlan Prepare(AddonPackage package, PermissionGrant grant, NetworkSelections selections, RemoteInputRequest request)
    {
        grant.Demand("media.input"); grant.Demand("sessions.manage"); grant.Demand("network.connect");
        request.Validate();
        var selected = selections.Resolve(package, grant, request.DestinationId);
        string? credential = request.UseCredential ? selections.Credential(package, grant, selected) : null;
        var plan = new RemoteInputPlan(selected.Destination with { Addresses = selected.Destination.Addresses.ToArray() },
            request.Path, credential is null ? null : selected.CredentialHeader, credential);
        plan.Validate(); return plan;
    }

    internal JsonObject ToPrivateJson() => new()
    {
        ["destination"] = JsonSerializer.SerializeToNode(Destination, Contract.Json),
        ["path"] = path, ["header"] = Header, ["credential"] = Credential,
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
            value["credential"] is null ? null : Contract.Text(value, "credential", 4096));
        plan.Validate(); return plan;
    }
}
