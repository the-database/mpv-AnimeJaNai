using System.Net;
using System.Text.Json;

namespace AnimeJaNai.Addons;

// Trusted resource configuration, never bind instructions supplied by a guest.
public sealed record ListenerCors(string[] Origins, string[] Methods, string[] Headers, string[] ExposeHeaders, bool AllowCredentials = false);
public sealed record ListenerBinding(string Address, int Port, string[] SensitiveHeaders, string[] SensitiveQuery,
    string Scheme = "http", string Scope = "loopback", string? CertificateId = null, string? CertificateHost = null,
    string? PublicBaseUrl = null, string[]? AllowedHosts = null, ListenerCors? Cors = null)
{
    public void Validate()
    {
        Contract.Require(IPAddress.TryParse(Address, out var ip) && !ip.IsIPv4MappedToIPv6 && Address == ip.ToString() &&
            (NetworkDestination.IsUnicast(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) && Port is >= 1 and <= 65535 &&
            Scope is "loopback" or "lan" or "public" && (Scope != "loopback" || IPAddress.IsLoopback(ip)),
            "invalid_listener", "Choose an explicit IP, port and loopback/LAN/public scope.");
        Contract.Require(Scheme is "http" or "https" && (Scheme == "https" ? Guid.TryParseExact(CertificateId, "N", out _) && ValidHost(CertificateHost) : CertificateId is null && CertificateHost is null),
            "invalid_listener", "HTTPS requires an imported certificate and a certificate hostname.");
        Contract.Require(AllowedHosts is null || AllowedHosts.Length is > 0 and <= 16 && AllowedHosts.All(ValidHost), "invalid_listener", "Use up to sixteen exact allowed hostnames or IP addresses.");
        Contract.Require(Address is not ("0.0.0.0" or "::") || AllowedHosts is { Length: > 0 }, "invalid_listener", "Specify the hostname or IP clients will use when binding all interfaces.");
        if (PublicBaseUrl is not null)
            Contract.Require(ValidOrigin(PublicBaseUrl), "invalid_listener", "Enter a public HTTP(S) origin without a path, credentials or query.");
        if (Cors is { } cors)
        {
            Contract.Require(cors.Origins is { Length: > 0 and <= 16 } && cors.Origins.All(ValidOrigin) &&
                cors.Methods is { Length: > 0 and <= 7 } && cors.Methods.All(m => m is "GET" or "HEAD" or "POST" or "PUT" or "PATCH" or "DELETE" or "OPTIONS"),
                "invalid_listener", "Choose exact browser origins and allowed HTTP methods.");
            ValidateNames(cors.Headers); ValidateNames(cors.ExposeHeaders);
        }
        ValidateNames(SensitiveHeaders); ValidateNames(SensitiveQuery);
    }
    internal static bool ValidHost(string? host) => host is { Length: > 0 and <= 253 } && !host.Any(char.IsControl) &&
        !host.Contains('*') && !host.Contains('%') && !host.EndsWith('.') && (IPAddress.TryParse(host, out _) || Uri.CheckHostName(host) == UriHostNameType.Dns);
    internal static bool ValidOrigin(string origin) => Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
        uri.UserInfo.Length == 0 && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 && ValidHost(uri.IdnHost.Trim('[', ']')) &&
        string.Equals(origin.TrimEnd('/'), uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
    private static void ValidateNames(string[] names) => Contract.Require(names is { Length: <= 32 } &&
        names.All(n => n is { Length: > 0 and <= 64 } && n.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) &&
        names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Length,
        "invalid_listener", "Use at most 32 distinct sensitive field names.");
    public ListenerBinding Copy() => this with { SensitiveHeaders = [.. SensitiveHeaders], SensitiveQuery = [.. SensitiveQuery], AllowedHosts = AllowedHosts is null ? null : [.. AllowedHosts],
        Cors = Cors is null ? null : Cors with { Origins = [.. Cors.Origins], Methods = [.. Cors.Methods], Headers = [.. Cors.Headers], ExposeHeaders = [.. Cors.ExposeHeaders] } };
}

public sealed record ApprovedListener(string Id, string Name, ListenerBinding Binding);

public sealed class ListenerSelections(string root)
{
    internal ListenerCertificates Certificates { get; } = new(root);
    private sealed record State(int Version, string Hash, ApprovedListener[] Listeners);
    private readonly object sync = new();
    private string DirectoryFor(AddonPackage package, PermissionGrant grant)
    {
        Contract.Require(package.Hash == grant.PackageHash, "invalid_grant", "Listener consent belongs to a different package.");
        grant.Demand("network.listen");
        return SafeFiles.DirectoryPath(root, "listener-approvals", package.Manifest.Id);
    }
    public ApprovedListener[] List(AddonPackage package, PermissionGrant grant)
    {
        lock (sync) return Read(DirectoryFor(package, grant), package.Hash).Listeners
            .Select(l => l with { Binding = l.Binding.Copy() }).ToArray();
    }
    public ApprovedListener Resolve(AddonPackage package, PermissionGrant grant, string id)
    {
        var found = List(package, grant).FirstOrDefault(l => l.Id == id);
        Contract.Require(found is not null, "listener_not_granted", "This listener is not approved for this addon version.");
        return found;
    }
    public string Approve(AddonPackage package, PermissionGrant grant, string name, ListenerBinding binding)
    {
        binding.Validate();
        Contract.Require(name is { Length: > 0 and <= 100 } && !name.Any(char.IsControl), "invalid_listener", "Use a listener name of 1–100 characters.");
        lock (sync)
        {
            string directory = DirectoryFor(package, grant);
            using var held = SafeFiles.Lock(directory);
            var state = Read(directory, package.Hash);
            Contract.Require(state.Listeners.Length < 4, "capacity_exceeded", "At most four listeners can be approved per addon.");
            string id = Guid.NewGuid().ToString("N");
            Write(directory, state with { Listeners = [.. state.Listeners, new(id, name, binding.Copy())] });
            return id;
        }
    }
    // Caller must stop/drain the worker before revoking its approved resources.
    public void Revoke(AddonPackage package, PermissionGrant grant, string id)
    {
        lock (sync)
        {
            string directory = DirectoryFor(package, grant);
            using var held = SafeFiles.Lock(directory);
            var state = Read(directory, package.Hash);
            Write(directory, state with { Listeners = state.Listeners.Where(l => l.Id != id).ToArray() });
        }
    }
    public void Clear(string addonId)
    {
        Contract.Require(Contract.ValidId(addonId), "invalid_id", "Invalid addon ID.");
        lock (sync)
        {
            string directory = SafeFiles.DirectoryPath(root, "listener-approvals", addonId);
            using var held = SafeFiles.Lock(directory);
            string path = Path.Combine(directory, "approvals.json"); SafeFiles.CheckParents(path); File.Delete(path);
        }
    }
    private static State Read(string directory, string hash)
    {
        string path = Path.Combine(directory, "approvals.json"); SafeFiles.CheckParents(path);
        if (!File.Exists(path)) return new(1, hash, []);
        try
        {
            var state = Contract.ParseObject(AddonPackage.ReadBoundedFile(path, 64 * 1024)).Deserialize<State>(Contract.Json);
            Contract.Require(state is not null && state.Version == 1 && state.Hash is not null && Contract.ValidHash(state.Hash) &&
                state.Listeners is { Length: <= 4 } && state.Listeners.All(l => l is not null && l.Binding is not null &&
                    Guid.TryParseExact(l.Id, "N", out _) && l.Name is { Length: > 0 and <= 100 } && !l.Name.Any(char.IsControl)) &&
                state.Listeners.Select(l => l.Id).Distinct(StringComparer.Ordinal).Count() == state.Listeners.Length,
                "invalid_approvals", "Invalid listener approvals.");
            foreach (var listener in state.Listeners) listener.Binding.Validate();
            return state.Hash == hash ? state : new(1, hash, []);
        }
        catch (Exception error) when (error is JsonException or AddonException or ArgumentException)
        { throw new AddonException("invalid_approvals", "Listener approvals are invalid and have been preserved for recovery."); }
    }
    private static void Write(string directory, State state) => SafeFiles.AtomicWrite(Path.Combine(directory, "approvals.json"), JsonSerializer.SerializeToUtf8Bytes(state, Contract.Json));
}
