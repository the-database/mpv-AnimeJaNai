using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons;

public sealed record NetworkDestination(string Origin, string Scheme, string Host, int Port, string[] Addresses)
{
    public JsonObject Describe() => new()
    {
        ["origin"] = Origin, ["protocol"] = Scheme,
        ["addresses"] = new JsonArray(Addresses.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
    };

    public static (string Origin, string Scheme, string Host, int Port) Parse(string text)
    {
        Contract.Require(text.Length is > 0 and <= 512 && !text.Any(char.IsControl) && !text.Contains('\\') &&
            Uri.TryCreate(text, UriKind.Absolute, out _), "invalid_destination", "Enter an HTTP, HTTPS or UDP service address.");
        var uri = new Uri(text, UriKind.Absolute);
        Contract.Require(uri.Scheme is "http" or "https" or "udp" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
            uri.AbsolutePath is "" or "/" && uri.Host.Length is > 0 and <= 253 && uri.Port is >= 1 and <= 65535 &&
            !uri.Host.Contains('%'), "invalid_destination", "Approve a service origin with a port, without a path, credentials or wildcard.");
        string host = uri.IdnHost.ToLowerInvariant();
        Contract.Require(!host.Contains('*') && !host.EndsWith('.') && Uri.CheckHostName(host) != UriHostNameType.Unknown,
            "invalid_destination", "Invalid service hostname.");
        if (IPAddress.TryParse(host.Trim('[', ']'), out var address))
        {
            Contract.Require(IsUnicast(address), "invalid_destination", "Select a unicast service address.");
            host = address.ToString();
        }
        string authorityHost = host.Contains(':') ? "[" + host + "]" : host;
        return ($"{uri.Scheme}://{authorityHost}:{uri.Port}", uri.Scheme, host, uri.Port);
    }

    public static async Task<NetworkDestination> InspectAsync(string text, CancellationToken token)
    {
        var parsed = Parse(text);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        IPAddress[] resolved;
        try { resolved = IPAddress.TryParse(parsed.Host, out var address) ? [address] : await Dns.GetHostAddressesAsync(parsed.Host, deadline.Token); }
        catch (Exception error) when (error is SocketException or OperationCanceledException)
        { throw new AddonException("destination_unavailable", "Could not resolve the service address. Check it and try again."); }
        var addresses = resolved.Select(a => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).Distinct().ToArray();
        Contract.Require(addresses.Length is > 0 and <= 8 && addresses.All(IsUnicast), "invalid_destination", "The service must resolve to at most eight unicast addresses.");
        return new(parsed.Origin, parsed.Scheme, parsed.Host, parsed.Port, addresses.Select(a => a.ToString()).Order(StringComparer.Ordinal).ToArray());
    }

    internal static bool IsUnicast(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => bytes[0] is > 0 and < 224 && !address.Equals(IPAddress.Broadcast),
            AddressFamily.InterNetworkV6 => address.ScopeId == 0 && !address.Equals(IPAddress.IPv6Any) && !address.IsIPv6Multicast,
            _ => false,
        };
    }
}

public sealed record ApprovedDestination(string Id, string Name, NetworkDestination Destination, string? CredentialHeader = null, string? ProtectedCredential = null);

// One service shares this object with its workers. Cached immutable snapshots
// avoid filesystem I/O on the datagram path; writes become visible only after
// the atomic save succeeds. The host's existing lease serializes offline edits.
public sealed class NetworkSelections(string root)
{
    private sealed record State(int Version, string Hash, ApprovedDestination[] Destinations);
    private readonly object sync = new();
    private readonly Dictionary<string, State> cache = new(StringComparer.Ordinal);
    private string DirectoryFor(AddonPackage package, PermissionGrant grant)
    {
        Contract.Require(package.Hash == grant.PackageHash, "invalid_grant", "Destination consent belongs to another package.");
        grant.Demand("network.connect");
        return SafeFiles.DirectoryPath(root, "network-approvals", package.Manifest.Id);
    }

    public JsonObject List(AddonPackage package, PermissionGrant grant)
    {
        lock (sync)
        {
            var state = Read(DirectoryFor(package, grant), package.Hash);
            return new() { ["destinations"] = new JsonArray(state.Destinations.Select(d =>
            {
                var item = d.Destination.Describe(); item["id"] = d.Id; item["name"] = d.Name;
                item["credentialHeader"] = d.CredentialHeader; item["hasCredential"] = d.ProtectedCredential is not null;
                return (JsonNode?)item;
            }).ToArray()) };
        }
    }

    public string Approve(AddonPackage package, PermissionGrant grant, string name, NetworkDestination destination)
    {
        ValidateDestination(destination);
        Contract.Require(name.Length is > 0 and <= 100 && !name.Any(char.IsControl), "invalid_destination", "Use a service name of 1–100 characters.");
        lock (sync)
        {
            string directory = DirectoryFor(package, grant);
            var state = Read(directory, package.Hash);
            Contract.Require(state.Destinations.Length < 8, "capacity_exceeded", "At most eight service destinations per addon are supported.");
            string id = Guid.NewGuid().ToString("N");
            Write(directory, state with { Destinations = [.. state.Destinations, new(id, name, destination with { Addresses = [.. destination.Addresses] })] });
            return id;
        }
    }

    public ApprovedDestination Resolve(AddonPackage package, PermissionGrant grant, string id)
    {
        lock (sync)
        {
            var found = Read(DirectoryFor(package, grant), package.Hash).Destinations.FirstOrDefault(d => d.Id == id);
            Contract.Require(found is not null, "destination_not_granted", "This service is not approved for this addon version.");
            return found with { Destination = found.Destination with { Addresses = [.. found.Destination.Addresses] } };
        }
    }

    public void ValidateCredential(AddonPackage package, PermissionGrant grant, string id, string header, string value)
    {
        grant.Demand("credentials.use");
        NetworkAccess.ValidateHeader(header, value);
        Contract.Require(value.Length is > 0 and <= 4096, "invalid_credential", "Enter a nonempty credential of at most 4096 characters.");
        var selected = Resolve(package, grant, id);
        Contract.Require(selected.Destination.Scheme is "http" or "https", "invalid_credential", "Saved header credentials require an HTTP service.");
        Contract.Require(OperatingSystem.IsWindows(), "feature_unavailable", "Protected credentials currently require Windows.");
    }

    public void SetCredential(AddonPackage package, PermissionGrant grant, string id, string header, string value)
    {
        ValidateCredential(package, grant, id, header, value);
        lock (sync)
        {
            string directory = DirectoryFor(package, grant); var state = Read(directory, package.Hash);
            _ = Resolve(package, grant, id);
            string protectedValue = WindowsSecretProtection.Protect(value, Context(package, id));
            Write(directory, state with { Destinations = state.Destinations.Select(d => d.Id == id ? d with { CredentialHeader = header, ProtectedCredential = protectedValue } : d).ToArray() });
        }
    }

    public string Credential(AddonPackage package, PermissionGrant grant, ApprovedDestination destination)
    {
        grant.Demand("credentials.use");
        Contract.Require(destination.ProtectedCredential is not null, "credential_not_granted", "No credential is saved for this destination.");
        return WindowsSecretProtection.Unprotect(destination.ProtectedCredential, Context(package, destination.Id));
    }

    // The management caller drains the addon before changing existing authority.
    public void Revoke(AddonPackage package, PermissionGrant grant, string id, bool credentialOnly = false)
    {
        lock (sync)
        {
            string directory = DirectoryFor(package, grant); var state = Read(directory, package.Hash);
            Write(directory, state with { Destinations = credentialOnly
                ? state.Destinations.Select(d => d.Id == id ? d with { CredentialHeader = null, ProtectedCredential = null } : d).ToArray()
                : state.Destinations.Where(d => d.Id != id).ToArray() });
        }
    }

    public void Clear(string addonId)
    {
        Contract.Require(Contract.ValidId(addonId), "invalid_id", "Invalid addon id.");
        lock (sync)
        {
            string directory = SafeFiles.DirectoryPath(root, "network-approvals", addonId);
            using var held = SafeFiles.Lock(directory);
            string path = Path.Combine(directory, "approvals.json"); SafeFiles.CheckParents(path); File.Delete(path);
            cache.Remove(directory);
        }
    }

    private static string Context(AddonPackage package, string id) => package.Manifest.Id + "/" + package.Hash + "/" + id;

    private State Read(string directory, string hash)
    {
        if (!cache.TryGetValue(directory, out var state))
        {
            string path = Path.Combine(directory, "approvals.json"); SafeFiles.CheckParents(path);
            if (!File.Exists(path)) return new(1, hash, []);
            try
            {
                state = Contract.ParseObject(AddonPackage.ReadBoundedFile(path, 256 * 1024)).Deserialize<State>(Contract.Json);
                Contract.Require(state is not null && state.Version == 1 && state.Hash is not null && Contract.ValidHash(state.Hash) &&
                    state.Destinations is { Length: <= 8 } && state.Destinations.All(d => d is not null) &&
                    state.Destinations.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() == state.Destinations.Length,
                    "invalid_approvals", "Invalid destination approvals.");
                foreach (var d in state.Destinations)
                {
                    ValidateDestination(d.Destination);
                    Contract.Require(Guid.TryParseExact(d.Id, "N", out _) && d.Name is { Length: > 0 and <= 100 } && !d.Name.Any(char.IsControl) &&
                        (d.ProtectedCredential is null ? d.CredentialHeader is null : d.ProtectedCredential.Length <= 16000 && d.CredentialHeader is not null && d.Destination.Scheme is "http" or "https"),
                        "invalid_approvals", "Invalid destination approval.");
                    if (d.CredentialHeader is not null) NetworkAccess.ValidateHeader(d.CredentialHeader, "present");
                }
            }
            catch (Exception e) when (e is AddonException or JsonException or ArgumentException)
            { throw new AddonException("invalid_approvals", "Destination approvals are invalid and have been preserved for recovery."); }
            Contract.Require(cache.Count < 128, "capacity_exceeded", "Too many destination stores in this host.");
            cache[directory] = state;
        }
        return state.Hash == hash ? state : new(1, hash, []);
    }

    private void Write(string directory, State state)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state, Contract.Json);
        Contract.Require(bytes.Length <= 256 * 1024, "capacity_exceeded", "Destination approvals exceed 256 KiB.");
        using var held = SafeFiles.Lock(directory);
        SafeFiles.AtomicWrite(Path.Combine(directory, "approvals.json"), bytes); cache[directory] = state;
    }

    internal static void ValidateDestination(NetworkDestination destination)
    {
        Contract.Require(destination is not null && destination.Origin is not null && destination.Addresses is { Length: > 0 and <= 8 },
            "invalid_destination", "Invalid resolved destination.");
        var parsed = NetworkDestination.Parse(destination.Origin);
        Contract.Require(parsed.Origin == destination.Origin && parsed.Scheme == destination.Scheme && parsed.Host == destination.Host && parsed.Port == destination.Port &&
            destination.Addresses.All(a => a is not null && IPAddress.TryParse(a, out var address) && NetworkDestination.IsUnicast(address)),
            "invalid_destination", "Invalid resolved destination.");
    }
}
