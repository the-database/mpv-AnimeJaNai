using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace AnimeJaNai.Addons;

// Each instance belongs to one Wasm worker. HTTP contexts and response streams
// stay here. No socket, context, certificate or process handle crosses the SDK.
public sealed partial class HttpServerAccess(AddonPackage package, PermissionGrant grant, ListenerSelections selections) : IAsyncDisposable
{
    public const int MaximumInlineBytes = 32 * 1024;
    public const int MaximumRequests = 64;
    private static readonly SemaphoreSlim HostListeners = new(16, 16);
    private readonly object sync = new();
    private readonly Dictionary<string, Server> servers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Pending> requests = new(StringComparer.Ordinal);
    private readonly Channel<(string Name, JsonObject Data)> events = Channel.CreateBounded<(string, JsonObject)>(128);
    private bool disposed;
    internal TimeSpan DecisionTimeout { get; init; } = TimeSpan.FromSeconds(15);
    internal bool HasListeners { get { lock (sync) return servers.Count > 0; } }

    private sealed class Server(string id, ApprovedListener selection)
    {
        public readonly string Id = id;
        public readonly ApprovedListener Selection = selection;
        public readonly CancellationTokenSource Lifetime = new();
        public WebApplication? Application;
        public ListenerCertificates.Material? Certificate;
        public Task Opening = Task.CompletedTask;
        public Task? Closing;
        public string State = "opening";
        public string? Error;
        public bool Slot;
    }
    private sealed record Response(int Status, KeyValuePair<string, string[]>[] Headers, byte[] Body,
        Func<HttpContext, CancellationToken, Task>? Native = null);
    private sealed class Pending(string id, Server server, HttpContext context)
    {
        public readonly string Id = id;
        public readonly Server Server = server;
        public readonly HttpContext Context = context;
        public readonly TaskCompletionSource<Response> Response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly CancellationTokenSource Lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, server.Lifetime.Token);
        public readonly CancellationTokenSource Decision = new();
        public readonly long Created = System.Diagnostics.Stopwatch.GetTimestamp();
        public double DecisionSeconds;
        public Task? BodyTask;
        public byte[]? Body;
        public string? BodyError;
        public MemoryStream? ResponseBuffer;
        public int ResponseStatus;
        public KeyValuePair<string, string[]>[]? ResponseHeaders;
        public bool Claimed;
    }
    public JsonObject List()
    {
        Demand();
        return new() { ["listeners"] = JsonSerializer.SerializeToNode(selections.List(package, grant), Contract.Json) };
    }
    public JsonObject Formats()
    {
        Demand();
        return new() { ["protocols"] = OperatingSystem.IsWindows() ? new JsonArray("http", "https") : new JsonArray("http"), ["scope"] = "approved", ["maximumInlineBytes"] = MaximumInlineBytes,
            ["maximumRequests"] = MaximumRequests, ["maximumListeners"] = 4, ["maximumConnectionsPerListener"] = 128,
            ["maximumActiveRequests"] = 128,
            ["decisionSeconds"] = 15, ["maximumDecisionSeconds"] = 120, ["maximumBufferedBytes"] = MaximumBufferedBytes,
            ["requestBodyReading"] = true, ["webSockets"] = true, ["cors"] = "reviewed-or-deny" };
    }
    public string Open(string listenerId)
    {
        Demand();
        var selected = selections.Resolve(package, grant, listenerId);
        lock (sync)
        {
            Contract.Require(!disposed, "owner_closed", "Addon has stopped.");
            Contract.Require(servers.Count < 4 && !servers.Values.Any(s => s.Selection.Id == listenerId), "capacity_exceeded", "This listener is already open or the addon listener limit is reached.");
            Contract.Require(HostListeners.Wait(0), "capacity_exceeded", "The host listener limit is reached.");
            var server = new Server(Guid.NewGuid().ToString("N"), selected) { Slot = true };
            servers.Add(server.Id, server);
            server.Opening = Task.Run(() => StartAsync(server));
            return server.Id;
        }
    }
    private async Task StartAsync(Server server)
    {
        try
        {
            if (server.Selection.Binding.Scheme == "https")
            {
                server.Certificate = selections.Certificates.Open(package, grant, server.Selection.Binding.CertificateId!, serving: true);
                ListenerCertificates.Validate(server.Certificate.Certificate, server.Selection.Binding.CertificateHost);
            }
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(HttpServerAccess).Assembly.GetName().Name });
            builder.Logging.ClearProviders(); // Never log request URLs, headers or upstream bodies.
            builder.WebHost.UseKestrel(options =>
            {
                options.AddServerHeader = false;
                options.Limits.MaxRequestHeadersTotalSize = 64 * 1024;
                options.Limits.MaxRequestLineSize = 8192;
                options.Limits.MaxRequestBodySize = null; // Native forwarding is bounded separately; buffered reads enforce 256 KiB.
                options.Limits.MaxConcurrentConnections = 128;
                options.Limits.MaxConcurrentUpgradedConnections = 16;
                options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
                options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
                options.Listen(IPAddress.Parse(server.Selection.Binding.Address), server.Selection.Binding.Port,
                    endpoint =>
                    {
                        endpoint.Protocols = HttpProtocols.Http1;
                        if (server.Certificate is { } certificate) endpoint.UseHttps(https =>
                        {
                            https.ServerCertificate = certificate.Certificate;
                            https.ServerCertificateChain = certificate.All;
                        });
                    });
            });
            var app = builder.Build();
            lock (sync) server.Application = app;
            app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30), KeepAliveTimeout = TimeSpan.FromSeconds(20) });
            app.Run(context => HandleAsync(server, context));
            await app.StartAsync(server.Lifetime.Token);
            lock (sync) { if (server.State == "opening") server.State = "listening"; }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            lock (sync)
            {
                if (server.State == "opening") { server.State = "failed"; server.Error = error is AddonException addon ? addon.Code : "listener_unavailable"; }
            }
        }
    }
    public JsonObject Status(string serverId)
    {
        Demand();
        lock (sync)
        {
            var server = Find(serverId);
            return new() { ["serverId"] = server.Id, ["listenerId"] = server.Selection.Id, ["state"] = server.State,
                ["publicBaseUrl"] = server.Selection.Binding.PublicBaseUrl, ["reachability"] = "unverified",
                ["certificateExpires"] = server.Certificate?.Certificate.NotAfter.ToUniversalTime().ToString("O"),
                ["error"] = server.Error is null ? null : new JsonObject { ["code"] = server.Error, ["message"] = "Could not open the approved listener. Check its port and certificate, then restart it." } };
        }
    }
    public (string Name, JsonObject Data)? TakeEvent()
    {
        while (events.Reader.TryRead(out var item))
        {
            lock (sync)
            {
                if (item.Name == "http.request" && (!requests.TryGetValue(item.Data["requestId"]!.GetValue<string>(), out var request) || request.Claimed)) continue;
                return item;
            }
        }
        return null;
    }
    private async Task HandleAsync(Server server, HttpContext context)
    {
        // Listener/CORS consent does not authenticate a caller. Addons authorize
        // each request before claiming a response or a stream resource.
        if (!AuthorizeEndpoint(server.Selection.Binding, context)) return;
        if (context.Request.Headers.ContainsKey("Upgrade") && !context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 501; return; }
        Pending pending;
        lock (sync)
        {
            if (disposed || server.State is "closing" or "closed" || requests.Count >= 128 || requests.Values.Count(r => !r.Claimed) >= MaximumRequests)
            { context.Response.StatusCode = 503; return; }
            pending = new(Guid.NewGuid().ToString("N"), server, context) { DecisionSeconds = DecisionTimeout.TotalSeconds };
            JsonObject metadata = Describe(pending);
            // JSON escaping can multiply header size; reserve space for the
            // outer RPC event rather than relying on raw HTTP header limits.
            if (Encoding.UTF8.GetByteCount(metadata.ToJsonString()) > 64 * 1024) { DisposeRequest(pending); context.Response.StatusCode = 431; return; }
            requests.Add(pending.Id, pending);
            if (!events.Writer.TryWrite(("http.request", metadata)))
            { requests.Remove(pending.Id); DisposeRequest(pending); context.Response.StatusCode = 503; return; }
            pending.Decision.CancelAfter(DecisionTimeout);
        }
        using var decision = CancellationTokenSource.CreateLinkedTokenSource(pending.Lifetime.Token, pending.Decision.Token);
        string reason = "completed";
        try
        {
            Response response;
            try { response = await pending.Response.Task.WaitAsync(decision.Token); }
            catch (OperationCanceledException) when (!pending.Lifetime.IsCancellationRequested)
            { reason = "request_expired"; context.Response.StatusCode = 504; return; }
            if (response.Native is { } native)
            {
                await native(context, pending.Lifetime.Token);
                return;
            }
            context.Response.StatusCode = response.Status;
            foreach (var header in response.Headers) context.Response.Headers[header.Key] = new StringValues(header.Value);
            if (response.Status is not (204 or 304)) context.Response.ContentLength = response.Body.Length;
            if (!HttpMethods.IsHead(context.Request.Method) && response.Body.Length > 0)
            {
                using var writeDeadline = CancellationTokenSource.CreateLinkedTokenSource(pending.Lifetime.Token);
                writeDeadline.CancelAfter(TimeSpan.FromSeconds(15));
                await context.Response.Body.WriteAsync(response.Body, writeDeadline.Token);
            }
        }
        catch (OperationCanceledException) { reason = "request_cancelled"; context.Abort(); }
        catch (IOException) { reason = "request_disconnected"; context.Abort(); }
        finally
        {
            lock (sync)
            {
                requests.Remove(pending.Id);
                pending.Lifetime.Cancel();
                // Lifecycle events are bounded hints. Request authority is
                // always checked against the live table, including expiry.
                events.Writer.TryWrite((reason == "completed" ? "http.closed" : "http.disconnected",
                    new JsonObject { ["kind"] = "request", ["requestId"] = pending.Id, ["serverId"] = server.Id, ["reason"] = reason }));
            }
            if (pending.BodyTask is { } bodyTask) await bodyTask;
            DisposeRequest(pending);
            pending.Done.TrySetResult();
        }
    }
    private JsonObject Describe(Pending request)
    {
        var binding = request.Server.Selection.Binding;
        var sensitiveHeaders = new HashSet<string>(binding.SensitiveHeaders, StringComparer.OrdinalIgnoreCase)
            { "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie" };
        var sensitiveQuery = new HashSet<string>(binding.SensitiveQuery, StringComparer.OrdinalIgnoreCase);
        sensitiveHeaders.UnionWith(package.Manifest.SensitiveRequestFields?.Headers ?? []);
        sensitiveQuery.UnionWith(package.Manifest.SensitiveRequestFields?.Query ?? []);
        JsonArray Fields(IEnumerable<KeyValuePair<string, StringValues>> fields, HashSet<string> sensitive) =>
            new(fields.Select(pair => (JsonNode?)new JsonObject { ["name"] = pair.Key,
                ["values"] = sensitive.Contains(pair.Key) ? null : new JsonArray(pair.Value.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
                ["redacted"] = sensitive.Contains(pair.Key) }).ToArray());
        return new()
        {
            ["requestId"] = request.Id, ["serverId"] = request.Server.Id, ["method"] = request.Context.Request.Method,
            ["path"] = request.Context.Request.Path.Value, ["query"] = Fields(request.Context.Request.Query, sensitiveQuery),
            ["headers"] = Fields(request.Context.Request.Headers, sensitiveHeaders),
            ["peerAddress"] = request.Context.Connection.RemoteIpAddress?.ToString(), ["webSocketRequested"] = request.Context.WebSockets.IsWebSocketRequest,
        };
    }
    public void Respond(string requestId, int status, KeyValuePair<string, string[]>[] headers, byte[] body)
    {
        Demand();
        Contract.Require(body.Length <= MaximumInlineBytes, "invalid_response", "Use chunks for responses exceeding 32 KiB.");
        ValidateResponse(status, headers, body.Length);
        lock (sync)
        {
            var request = FindRequest(requestId);
            Claim(request);
            request.Response.SetResult(new(status, CopyHeaders(headers), [.. body]));
        }
    }
    private static KeyValuePair<string, string[]>[] CopyHeaders(KeyValuePair<string, string[]>[] headers) =>
        headers.Select(h => new KeyValuePair<string, string[]>(h.Key, [.. h.Value])).ToArray();
    private static void ValidateResponse(int status, KeyValuePair<string, string[]>[] headers, int bodyLength)
    {
        Contract.Require(status is >= 200 and <= 599 && bodyLength <= MaximumBufferedBytes &&
            (status is not (204 or 304) || bodyLength == 0), "invalid_response", "Use a final status and a body of at most 256 KiB; 204/304 have no body.");
        Contract.Require(headers is { Length: <= 64 } && headers.Select(h => h.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() == headers.Length,
            "invalid_response", "Use at most 64 distinct response header names, with repeated values in arrays.");
        int size = 0;
        foreach (var header in headers)
        {
            Contract.Require(header.Key is { Length: > 0 and <= 128 } && header.Key.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c)) &&
                !new[] { "connection", "transfer-encoding", "content-length", "keep-alive", "upgrade", "trailer", "te", "proxy-authenticate", "proxy-authorization" }.Contains(header.Key, StringComparer.OrdinalIgnoreCase) &&
                !header.Key.StartsWith("access-control-", StringComparison.OrdinalIgnoreCase) && header.Value is { Length: > 0 and <= 32 },
                "invalid_response", "Invalid or host-controlled response header.");
            foreach (string value in header.Value)
            {
                Contract.Require(value is not null && value.All(c => c is >= ' ' and <= '~'), "invalid_response", "Response header values must be printable ASCII.");
                size += header.Key.Length + value.Length + 4;
            }
        }
        Contract.Require(size <= 16 * 1024, "invalid_response", "Response headers exceed 16 KiB.");
    }
    public void RequestClose(string serverId)
    {
        Demand();
        lock (sync) BeginClose(Find(serverId));
    }
    private void BeginClose(Server server)
    {
        if (server.Closing is not null) return;
        server.State = "closing";
        server.Lifetime.Cancel();
        server.Closing = Task.Run(async () =>
        {
            try
            {
                await server.Opening;
                if (server.Application is { } app)
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await app.StopAsync(deadline.Token);
                    await app.DisposeAsync();
                }
                lock (sync)
                {
                    servers.Remove(server.Id); server.State = "closed";
                    events.Writer.TryWrite(("http.closed", new JsonObject { ["kind"] = "listener", ["serverId"] = server.Id, ["reason"] = "closed" }));
                }
                server.Certificate?.Dispose(); server.Certificate = null;
                if (server.Slot) { server.Slot = false; HostListeners.Release(); }
            }
            catch
            {
                lock (sync) { server.State = "cleanup_failed"; server.Error = "cleanup_failed"; server.Closing = null; }
                throw;
            }
        });
    }
    public async ValueTask DisposeAsync()
    {
        Task[] closing;
        lock (sync)
        {
            disposed = true;
            foreach (var server in servers.Values) BeginClose(server);
            closing = servers.Values.Select(s => s.Closing!).ToArray();
        }
        await Task.WhenAll(closing);
        events.Writer.TryComplete();
    }
    private Server Find(string id)
    {
        Contract.Require(servers.TryGetValue(id, out var server), "server_not_found", "Server is closed or belongs to another addon instance.");
        return server;
    }
    private void Demand() => grant.Demand("network.listen");
}
