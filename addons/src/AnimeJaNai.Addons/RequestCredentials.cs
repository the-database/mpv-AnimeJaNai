using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;

namespace AnimeJaNai.Addons;

// Capture is deliberately separate from authentication: the addon must validate
// the client with the selected upstream before returning protected content.
public sealed class RequestCredentials(AddonPackage package, PermissionGrant grant, NetworkSelections selections, HttpServerAccess servers) : IDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<string, Context> contexts = new(StringComparer.Ordinal);
    private bool disposed;
    private sealed record Field(string Kind, string Name, string[] Values);
    private sealed class Context(string destination, Field[] fields, int seconds)
    {
        public readonly string Destination = destination;
        public readonly Field[] Fields = fields;
        public readonly DateTimeOffset Expires = DateTimeOffset.UtcNow.AddSeconds(seconds);
        public readonly CancellationTokenSource Stop = new(TimeSpan.FromSeconds(seconds));
        public int Leases;
        public bool Released;
    }
    public JsonObject Formats()
    {
        Demand(); return new() { ["maximumContexts"] = 64, ["maximumLifetimeSeconds"] = 86400, ["maximumFields"] = 8, ["maximumBytes"] = 16384,
            ["captureAuthenticatesClient"] = false };
    }
    public string Capture(string requestId, string destinationId, JsonArray mappings, int seconds)
    {
        Demand();
        Contract.Require(seconds is >= 1 and <= 86400 && mappings.Count is > 0 and <= 8, "invalid_credential", "Capture up to eight fields for 1–86400 seconds.");
        var destination = selections.Resolve(package, grant, destinationId);
        Contract.Require(destination.Destination.Scheme is "http" or "https", "invalid_credential", "Choose an approved HTTP service.");
        var fields = new List<Field>(); int size = 0;
        foreach (var node in mappings)
        {
            Contract.Require(node is JsonObject, "invalid_credential", "Expected credential field mappings.");
            var mapping = (JsonObject)node!;
            string from = Contract.Text(mapping, "from", 8), name = Contract.Text(mapping, "name", 64), to = Contract.Text(mapping, "to", 8), target = Contract.Text(mapping, "target", 64);
            Contract.Require(to is "header" or "query" && target.Length > 0 && target.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'),
                "invalid_credential", "Use a header or query placement with a simple field name.");
            string[] values = servers.CaptureField(requestId, from, name);
            if (to == "header") foreach (string value in values) NetworkAccess.ValidateHeader(target, value);
            Contract.Require(!fields.Any(f => f.Kind == to && f.Name.Equals(target, StringComparison.OrdinalIgnoreCase)), "invalid_credential", "Choose only one source per credential field.");
            size += values.Sum(System.Text.Encoding.UTF8.GetByteCount);
            fields.Add(new(to, target, values));
        }
        Contract.Require(size <= 16384, "invalid_credential", "Captured credentials exceed 16 KiB.");
        lock (sync)
        {
            Demand(); Prune();
            Contract.Require(contexts.Count < 64, "capacity_exceeded", "Release unused credential contexts before capturing another.");
            string id = Guid.NewGuid().ToString("N"); contexts.Add(id, new(destinationId, fields.ToArray(), seconds)); return id;
        }
    }
    public JsonObject Status(string id)
    {
        lock (sync)
        {
            var context = Find(id);
            return new() { ["credentialId"] = id, ["destinationId"] = context.Destination, ["expires"] = context.Expires.ToString("O"),
                ["present"] = true, ["authenticated"] = false,
                ["fields"] = new JsonArray(context.Fields.Select(f => (JsonNode?)new JsonObject { ["kind"] = f.Kind, ["name"] = f.Name, ["present"] = true }).ToArray()) };
        }
    }
    public void Release(string id)
    {
        lock (sync)
        {
            Demand();
            Contract.Require(contexts.TryGetValue(id, out var context) && !context.Released, "credential_not_found", "Credential was released or belongs to another addon instance.");
            Retire(id, context);
        }
    }
    private void Retire(string id, Context context)
    {
        context.Released = true; context.Stop.Cancel();
        if (context.Leases == 0) { contexts.Remove(id); context.Stop.Dispose(); }
    }
    private void Prune()
    {
        foreach (var pair in contexts.Where(p => !p.Value.Released && p.Value.Expires <= DateTimeOffset.UtcNow).ToArray()) Retire(pair.Key, pair.Value);
    }
    private Context Find(string id)
    {
        Demand(); Prune();
        Contract.Require(contexts.TryGetValue(id, out var context) && !context.Released && !context.Stop.IsCancellationRequested,
            "credential_not_found", "Credential expired, was released or belongs to another addon instance."); return context;
    }
    internal Lease Acquire(string id, string destinationId)
    {
        lock (sync)
        {
            var context = Find(id);
            Contract.Require(context.Destination == destinationId, "credential_destination_mismatch", "This credential belongs to a different approved destination.");
            context.Leases++;
            return new(context.Stop.Token, context.Fields.Select(f => (f.Kind, f.Name, f.Values.ToArray())).ToArray(), () =>
            {
                lock (sync) if (--context.Leases == 0 && context.Released) { contexts.Remove(id); context.Stop.Dispose(); }
            });
        }
    }
    private void Demand()
    {
        grant.Demand("credentials.delegate"); grant.Demand("credentials.use"); grant.Demand("network.connect");
        Contract.Require(!disposed, "owner_closed", "Addon has stopped.");
    }
    public void Dispose()
    {
        lock (sync) { disposed = true; foreach (var pair in contexts.ToArray()) Retire(pair.Key, pair.Value); }
    }
    internal sealed class Lease(CancellationToken token, (string Kind, string Name, string[] Values)[] fields, Action release) : IDisposable
    {
        private Action? onRelease = release;
        public CancellationToken Token => token;
        internal void Apply(HttpRequestMessage request)
        {
            token.ThrowIfCancellationRequested();
            var uri = request.RequestUri!; var query = QueryHelpers.ParseQuery(uri.Query);
            string suffix = "";
            foreach (var field in fields)
            {
                if (field.Kind == "header")
                {
                    Contract.Require(!request.Headers.Contains(field.Name) && request.Content?.Headers.Contains(field.Name) != true,
                        "credential_conflict", "A delegated credential field already has a value.");
                    Contract.Require(request.Headers.TryAddWithoutValidation(field.Name, field.Values), "invalid_credential", "Delegated credentials require request headers.");
                }
                else
                {
                    Contract.Require(!query.ContainsKey(field.Name), "credential_conflict", "A delegated query field already has a value.");
                    foreach (string value in field.Values) suffix += "&" + Uri.EscapeDataString(field.Name) + "=" + Uri.EscapeDataString(value);
                }
            }
            if (suffix.Length > 0) request.RequestUri = new Uri(uri.AbsoluteUri + (uri.Query.Length == 0 ? "?" + suffix[1..] : suffix));
        }
        public void Dispose() => Interlocked.Exchange(ref onRelease, null)?.Invoke();
        public override string ToString() => "Private delegated credential lease";
    }
}
