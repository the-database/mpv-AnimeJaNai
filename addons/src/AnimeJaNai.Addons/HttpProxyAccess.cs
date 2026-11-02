using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace AnimeJaNai.Addons;

// All payload I/O stays native. Every operation owns a fresh pinned handler;
// credentials and cookies are never inherited from another request.
public sealed partial class HttpProxyAccess(AddonPackage package, PermissionGrant grant, NetworkSelections selections, HttpServerAccess servers, RequestCredentials credentials) : IAsyncDisposable
{
    public const int MaximumBufferBytes = 2 * 1024 * 1024;
    private const long MaximumTransferBytes = 512L * 1024 * 1024 * 1024;
    private static readonly SemaphoreSlim HostSlots = new(64, 64);
    private static readonly string[] HopHeaders = ["Connection", "Keep-Alive", "Transfer-Encoding", "TE", "Trailer", "Upgrade", "Proxy-Authenticate", "Proxy-Authorization"];
    private readonly object sync = new();
    private readonly Dictionary<string, Operation> operations = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;
    private sealed class Operation(CancellationToken token, bool buffered, RequestCredentials.Lease? credential)
    {
        public readonly CancellationTokenSource Stop = CancellationTokenSource.CreateLinkedTokenSource(token, credential?.Token ?? default);
        public readonly RequestCredentials.Lease? Credential = credential;
        public readonly bool Buffered = buffered;
        public Task Task = Task.CompletedTask;
        public string State = "pending";
        public string? Error;
        public int? Status;
        public long? RepresentationLength;
        public KeyValuePair<string, string[]>[] Headers = [];
        public byte[]? Body;
    }
    private sealed record Plan(ApprovedDestination Destination, Uri Target, KeyValuePair<string, string[]?>[] RequestHeaders,
        KeyValuePair<string, string[]?>[] ResponseHeaders, HashSet<string> PassRequest, HashSet<string> PassResponse, bool Credential, string? CredentialId);

    public JsonObject Formats()
    {
        Demand();
        return new() { ["nativeForwarding"] = true, ["webSockets"] = true, ["maximumBufferedBytes"] = MaximumBufferBytes,
            ["maximumChunkBytes"] = HttpServerAccess.MaximumInlineBytes, ["maximumOperations"] = 16, ["maximumBufferedOperations"] = 4,
            ["headerTimeoutSeconds"] = 15, ["ioTimeoutSeconds"] = 15, ["maximumLifetimeSeconds"] = 86400,
            ["maximumTransferBytes"] = MaximumTransferBytes, ["maximumWebSocketMessageBytes"] = 16 * 1024 * 1024 };
    }
    private Plan Parse(string destinationId, JsonObject options)
    {
        Demand();
        var destination = selections.Resolve(package, grant, destinationId);
        Contract.Require(destination.Destination.Scheme is "http" or "https", "invalid_request", "Choose an approved HTTP service.");
        var target = NetworkAccess.RequestTarget(destination.Destination, Contract.Text(options, "path", 2048));
        var requestHeaders = Headers(options["requestHeaders"]);
        var responseHeaders = Headers(options["responseHeaders"]);
        var passRequest = Names(options["passRequestHeaders"]);
        var passResponse = Names(options["passResponseHeaders"]);
        bool credential = options["useCredential"]?.GetValueKind() == System.Text.Json.JsonValueKind.True;
        Contract.Require(options["useCredential"] is null || options["useCredential"]?.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False,
            "invalid_request", "Choose whether to use the approved credential.");
        if (credential)
        {
            grant.Demand("credentials.use");
            Contract.Require(destination.ProtectedCredential is not null, "credential_not_granted", "No credential is saved for this service.");
            Contract.Require(!passRequest.Contains(destination.CredentialHeader!) && !requestHeaders.Any(h => h.Key.Equals(destination.CredentialHeader, StringComparison.OrdinalIgnoreCase)),
                "invalid_request", "Select exactly one source for a credential header.");
        }
        string? credentialId = options["credentialId"] is null ? null : Contract.Text(options, "credentialId", 64);
        Contract.Require(!credential || credentialId is null, "credential_conflict", "Choose a saved or delegated credential, not both.");
        return new(destination, target, requestHeaders, responseHeaders, passRequest, passResponse, credential, credentialId);
    }
    private static HashSet<string> Names(JsonNode? node)
    {
        if (node is null) return new(StringComparer.OrdinalIgnoreCase);
        Contract.Require(node is JsonArray { Count: <= 32 }, "invalid_request", "Use at most 32 pass-through header names.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in (JsonArray)node)
        {
            Contract.Require(item is JsonValue v && v.TryGetValue<string>(out _), "invalid_request", "Expected a header name.");
            string name = item!.GetValue<string>(); ValidateName(name); names.Add(name);
        }
        return names;
    }
    private static KeyValuePair<string, string[]?>[] Headers(JsonNode? node)
    {
        if (node is null) return [];
        Contract.Require(node is JsonArray { Count: <= 64 }, "invalid_request", "Use at most 64 header changes.");
        var headers = new Dictionary<string, string[]?>(StringComparer.OrdinalIgnoreCase); int size = 0;
        foreach (var item in (JsonArray)node)
        {
            Contract.Require(item is JsonObject, "invalid_request", "Expected header name and values.");
            string name = Contract.Text((JsonObject)item!, "name", 128); ValidateName(name);
            string[]? values = null;
            if (item!["values"] is not null)
            {
                Contract.Require(item["values"] is JsonArray { Count: > 0 and <= 32 }, "invalid_request", "Use a header value array or null to remove a header.");
                values = item["values"]!.AsArray().Select(v =>
                {
                    Contract.Require(v is JsonValue j && j.TryGetValue<string>(out _), "invalid_request", "Expected a header string.");
                    string value = v!.GetValue<string>();
                    Contract.Require(value.All(c => c is >= ' ' and <= '~'), "invalid_request", "Use printable ASCII header values.");
                    size += name.Length + value.Length + 4; return value;
                }).ToArray();
            }
            Contract.Require(headers.TryAdd(name, values), "invalid_request", "Duplicate header change.");
        }
        Contract.Require(size <= 16 * 1024, "invalid_request", "Header changes exceed 16 KiB.");
        return headers.ToArray();
    }
    private static void ValidateName(string name)
    {
        Contract.Require(name.Length is > 0 and <= 128 && name.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c)) &&
            !HopHeaders.Contains(name, StringComparer.OrdinalIgnoreCase) && !new[] { "Host", "Content-Length", "Expect" }.Contains(name, StringComparer.OrdinalIgnoreCase) &&
            !name.StartsWith("access-control-", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("sec-websocket-", StringComparison.OrdinalIgnoreCase),
            "invalid_request", "Invalid or host-controlled header.");
    }
    public string Forward(string requestId, string destinationId, JsonObject options)
    {
        var plan = Parse(destinationId, options);
        var sensitive = servers.SensitiveHeaders(requestId);
        lock (sync)
        {
            var (id, operation) = Reserve(false, plan);
            try
            {
                Task completed = servers.ClaimNative(requestId, async (context, token) =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, operation.Stop.Token);
                    linked.CancelAfter(TimeSpan.FromHours(24));
                    try
                    {
                        await ForwardAsync(operation, plan, sensitive, context, linked.Token);
                        lock (sync) operation.State = "completed";
                    }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        Fail(operation, error);
                        if (!context.Response.HasStarted) { context.Response.Clear(); context.Response.StatusCode = 502; }
                        else context.Abort();
                    }
                });
                operation.Task = FinishForwardAsync(operation, completed);
            }
            catch { operations.Remove(id); operation.Stop.Dispose(); operation.Credential?.Dispose(); HostSlots.Release(); throw; }
            return id;
        }
    }
    private async Task FinishForwardAsync(Operation operation, Task completed)
    {
        try
        {
            await completed;
            lock (sync) if (operation.State == "pending") { operation.State = "failed"; operation.Error = "proxy_cancelled"; }
        }
        finally { operation.Credential?.Dispose(); HostSlots.Release(); }
    }
    private async Task ForwardAsync(Operation operation, Plan plan, HashSet<string> sensitive, HttpContext context, CancellationToken token)
    {
        if (context.WebSockets.IsWebSocketRequest) { await TunnelAsync(operation, plan, sensitive, context, token); return; }
        using var handler = NetworkAccess.CreateHandler(plan.Destination.Destination);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        Contract.Require(context.Request.Method is "GET" or "HEAD" or "POST" or "PUT" or "PATCH" or "DELETE" or "OPTIONS", "invalid_request", "Unsupported HTTP method.");
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), plan.Target)
            { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        bool hasBody = context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding");
        using var headerWait = new HeaderWait(token);
        using var content = hasBody ? new NativeContent(context.Request.Body, context.Request.ContentLength, headerWait, token) : null;
        if (content is not null) request.Content = content;
        var excluded = Excluded(context.Request.Headers.Select(h => new KeyValuePair<string, IEnumerable<string>>(h.Key, h.Value.Select(v => v!))));
        excluded.UnionWith(["Host", "Content-Length", "Expect", "Forwarded", "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto"]);
        foreach (var header in context.Request.Headers)
        {
            if (excluded.Contains(header.Key) || sensitive.Contains(header.Key) && !plan.PassRequest.Contains(header.Key) ||
                plan.Credential && header.Key.Equals(plan.Destination.CredentialHeader, StringComparison.OrdinalIgnoreCase)) continue;
            AddHeader(request, header.Key, header.Value.Select(v => v!).ToArray());
        }
        ApplyRequest(request, plan, operation);
        try
        {
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerWait.Token);
        headerWait.Complete();
        // An upstream can reject a streaming upload before consuming it. Stop
        // its reader before the downstream HttpContext can be released.
        if (content is not null) await content.StopAsync();
        var headers = ResponseHeaders(response, plan);
        lock (sync) { operation.Status = (int)response.StatusCode; operation.RepresentationLength = response.Content.Headers.ContentLength; }
        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var header in headers) context.Response.Headers[header.Key] = new StringValues(header.Value);
        // Framing stays host-owned, but representation length matters for HEAD/ranges.
        if (response.Content.Headers.ContentLength is { } length && (int)response.StatusCode is not (204 or 304)) context.Response.ContentLength = length;
        if (context.Request.Method != "HEAD" && (int)response.StatusCode is not (204 or 304))
        {
            await using var body = await response.Content.ReadAsStreamAsync(token);
            await CopyAsync(body, context.Response.Body, MaximumTransferBytes, token);
        }
        }
        finally { if (content is not null) await content.StopAsync(); }
    }
    private void ApplyRequest(HttpRequestMessage request, Plan plan, Operation operation)
    {
        foreach (var header in plan.RequestHeaders)
        {
            request.Headers.Remove(header.Key); request.Content?.Headers.Remove(header.Key);
            if (header.Value is not null) AddHeader(request, header.Key, header.Value);
        }
        if (plan.Credential) AddHeader(request, plan.Destination.CredentialHeader!, [selections.Credential(package, grant, plan.Destination)]);
        operation.Credential?.Apply(request);
    }
    private static void AddHeader(HttpRequestMessage request, string name, string[] values)
    {
        if (request.Headers.TryAddWithoutValidation(name, values)) return;
        request.Content ??= new ByteArrayContent([]);
        Contract.Require(request.Content.Headers.TryAddWithoutValidation(name, values), "invalid_request", "Unsupported content header.");
    }
    private static HashSet<string> Excluded(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers)
    {
        var excluded = new HashSet<string>(HopHeaders, StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers.Where(h => h.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)))
            foreach (string value in header.Value) foreach (string name in value.Split(',')) excluded.Add(name.Trim());
        return excluded;
    }
    private static KeyValuePair<string, string[]>[] ResponseHeaders(HttpResponseMessage response, Plan plan)
    {
        var incoming = response.Headers.Concat(response.Content.Headers).ToArray(); var excluded = Excluded(incoming);
        excluded.Add("Content-Length");
        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in incoming)
        {
            if (excluded.Contains(header.Key) || header.Key.StartsWith("access-control-", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase) && !plan.PassResponse.Contains(header.Key)) continue;
            result[header.Key] = header.Value.ToArray();
        }
        foreach (var header in plan.ResponseHeaders)
        {
            if (header.Value is null) result.Remove(header.Key); else result[header.Key] = header.Value;
        }
        var output = result.ToArray();
        Contract.Require(output.Sum(h => h.Value.Sum(v => h.Key.Length + v.Length + 4)) <= 16 * 1024,
            "response_headers_too_large", "Resulting response headers exceed 16 KiB.");
        return output;
    }
    public string Buffer(string destinationId, JsonObject options)
    {
        var plan = Parse(destinationId, options);
        string method = options["method"] is null ? "GET" : Contract.Text(options, "method", 16);
        Contract.Require(method is "GET" or "HEAD" or "POST" or "PUT" or "PATCH" or "DELETE" or "OPTIONS", "invalid_request", "Unsupported HTTP method.");
        long maximum = options["maximumBytes"] is null ? MaximumBufferBytes : Contract.Number(options, "maximumBytes");
        Contract.Require(maximum is >= 1 and <= MaximumBufferBytes, "invalid_request", "Buffered responses are limited to 2 MiB.");
        byte[] body;
        try { body = options["bodyBase64"] is null ? [] : Convert.FromBase64String(Contract.Text(options, "bodyBase64", 44 * 1024)); }
        catch (FormatException) { throw new AddonException("invalid_request", "Expected base64 body bytes."); }
        Contract.Require(body.Length <= 32768 && (method is not ("GET" or "HEAD") || body.Length == 0), "invalid_request", "Invalid request body.");
        lock (sync)
        {
            var (id, operation) = Reserve(true, plan);
            operation.Task = Task.Run(async () =>
            {
                try
                {
                    operation.Stop.CancelAfter(TimeSpan.FromSeconds(30));
                    using var handler = NetworkAccess.CreateHandler(plan.Destination.Destination);
                    using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
                    using var request = new HttpRequestMessage(new HttpMethod(method), plan.Target)
                        { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                    if (body.Length > 0 || method is "POST" or "PUT" or "PATCH") request.Content = new ByteArrayContent(body);
                    ApplyRequest(request, plan, operation);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, operation.Stop.Token);
                    Contract.Require(method == "HEAD" || response.Content.Headers.ContentLength is null || response.Content.Headers.ContentLength <= maximum,
                        "response_too_large", "Upstream response exceeds the selected buffer limit.");
                    using var buffer = new MemoryStream();
                    await using var stream = await response.Content.ReadAsStreamAsync(operation.Stop.Token);
                    await CopyAsync(stream, buffer, maximum, operation.Stop.Token);
                    var headers = ResponseHeaders(response, plan); byte[] bytes = buffer.ToArray();
                    lock (sync) { operation.Status = (int)response.StatusCode; operation.RepresentationLength = response.Content.Headers.ContentLength; operation.Headers = headers; operation.Body = bytes; operation.State = "completed"; }
                }
                catch (Exception error) when (error is not OutOfMemoryException) { Fail(operation, error); }
                finally { operation.Credential?.Dispose(); HostSlots.Release(); }
            });
            return id;
        }
    }
    private (string Id, Operation Operation) Reserve(bool buffered, Plan plan)
    {
        Demand();
        Contract.Require(operations.Count < 16 && (!buffered || operations.Values.Count(o => o.Buffered) < 4), "capacity_exceeded", "Close completed proxy operations before creating more.");
        Contract.Require(HostSlots.Wait(0), "capacity_exceeded", "The host proxy capacity is full.");
        RequestCredentials.Lease? credential;
        try { credential = plan.CredentialId is null ? null : credentials.Acquire(plan.CredentialId, plan.Destination.Id); }
        catch { HostSlots.Release(); throw; }
        var operation = new Operation(lifetime.Token, buffered, credential); string id = Guid.NewGuid().ToString("N");
        operations.Add(id, operation); return (id, operation);
    }
    public JsonObject Status(string id)
    {
        lock (sync)
        {
            var operation = Find(id);
            return new() { ["operationId"] = id, ["state"] = operation.State, ["status"] = operation.Status,
                ["headers"] = new JsonArray(operation.Headers.Select(h => (JsonNode?)new JsonObject { ["name"] = h.Key, ["values"] = new JsonArray(h.Value.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) }).ToArray()),
                ["bodyLength"] = operation.Body?.Length, ["representationLength"] = operation.RepresentationLength, ["cleanupReady"] = operation.Task.IsCompleted,
                ["error"] = operation.Error is null ? null : new JsonObject { ["code"] = operation.Error, ["message"] = "The approved upstream operation did not complete." } };
        }
    }
    public BrokerResponse Read(string id, int offset, int count)
    {
        Contract.Require(offset is >= 0 and <= MaximumBufferBytes && count is >= 1 and <= 32768, "invalid_request", "Invalid buffer chunk offset or size.");
        lock (sync)
        {
            var operation = Find(id);
            Contract.Require(operation.State == "completed" && operation.Body is not null, "body_not_ready", "A complete buffered response is required.");
            Contract.Require(offset <= operation.Body.Length, "invalid_request", "Offset exceeds the body length.");
            int length = Math.Min(count, operation.Body.Length - offset);
            return new(new JsonObject { ["byteLength"] = length, ["offset"] = offset, ["totalBytes"] = operation.Body.Length,
                ["eof"] = offset + length == operation.Body.Length }, operation.Body.AsMemory(offset, length));
        }
    }
    public void Cancel(string id) { lock (sync) Find(id).Stop.Cancel(); }
    public void Close(string id)
    {
        lock (sync)
        {
            var operation = Find(id);
            Contract.Require(operation.Task.IsCompleted, "operation_active", "Cancel the operation and wait for completion before releasing it.");
            operations.Remove(id); operation.Stop.Dispose();
        }
    }
    private Operation Find(string id)
    {
        Demand();
        Contract.Require(operations.TryGetValue(id, out var operation), "operation_not_found", "Operation was closed or belongs to another addon instance."); return operation;
    }
    private void Fail(Operation operation, Exception error)
    {
        lock (sync) { operation.State = "failed"; operation.Error = error is AddonException a ? a.Code : error is OperationCanceledException ? "proxy_cancelled" : "proxy_unavailable"; }
    }
    private void Demand()
    {
        grant.Demand("network.proxy"); grant.Demand("network.connect");
        Contract.Require(!disposed, "owner_closed", "Addon has stopped.");
    }
    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        lock (sync) { disposed = true; lifetime.Cancel(); tasks = operations.Values.Select(o => o.Task).ToArray(); }
        await Task.WhenAll(tasks);
        lock (sync) { foreach (var operation in operations.Values) operation.Stop.Dispose(); operations.Clear(); }
    }
    internal static async Task CopyAsync(Stream input, Stream output, long maximum, CancellationToken token)
    {
        byte[] chunk = new byte[32768]; long transferred = 0;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        while (true)
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            int read = await input.ReadAsync(chunk, deadline.Token); if (read == 0) return;
            transferred += read;
            Contract.Require(transferred <= maximum, "response_too_large", "The transfer exceeded its byte limit.");
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            await output.WriteAsync(chunk.AsMemory(0, read), deadline.Token);
        }
    }
    private sealed class HeaderWait(CancellationToken token) : IDisposable
    {
        private readonly object gate = new();
        private readonly CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        private bool completed;
        public CancellationToken Token { get { lock (gate) { deadline.CancelAfter(TimeSpan.FromSeconds(15)); return deadline.Token; } } }
        public void UploadStarted() { lock (gate) deadline.CancelAfter(Timeout.InfiniteTimeSpan); }
        public void UploadFinished() { lock (gate) if (!completed) deadline.CancelAfter(TimeSpan.FromSeconds(15)); }
        public void Complete() { lock (gate) { completed = true; deadline.CancelAfter(Timeout.InfiniteTimeSpan); } }
        public void Dispose() => deadline.Dispose();
    }
    private sealed class NativeContent(Stream body, long? length, HeaderWait headerWait, CancellationToken lifetime) : HttpContent
    {
        private readonly object gate = new();
        private readonly CancellationTokenSource stop = new();
        private Task writing = Task.CompletedTask;
        protected override bool TryComputeLength(out long result) { result = length ?? 0; return length is not null; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Start(stream, default);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) => Start(stream, token);
        private Task Start(Stream stream, CancellationToken token) { lock (gate) return writing = WriteAsync(stream, token); }
        private async Task WriteAsync(Stream stream, CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, token, lifetime);
            linked.Token.ThrowIfCancellationRequested();
            headerWait.UploadStarted();
            try { await CopyAsync(body, stream, MaximumTransferBytes, linked.Token); }
            finally { headerWait.UploadFinished(); }
        }
        public async Task StopAsync()
        {
            Task task; lock (gate) { stop.Cancel(); task = writing; }
            try { await task; } catch (Exception e) when (e is OperationCanceledException or IOException or HttpRequestException) { }
        }
    }
}
