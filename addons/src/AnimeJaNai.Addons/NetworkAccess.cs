using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons;

// Guest-facing operations on explicit destination ids. A request owns its own
// handler; neither browser cookies, proxy settings nor Windows credentials are
// inherited. Only the reviewed IP addresses can be connected to.
public sealed class NetworkAccess(AddonPackage package, PermissionGrant grant, NetworkSelections selections) : IAsyncDisposable
{
    public const int MaxRequestBytes = 32 * 1024, MaxResponseBytes = 64 * 1024, MaxDatagramBytes = 16 * 1024;
    private sealed class Operation(CancellationToken lifetime)
    {
        public readonly CancellationTokenSource Stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        public Task<BrokerResponse> Task = System.Threading.Tasks.Task.FromResult(new BrokerResponse(null));
    }
    private static readonly SemaphoreSlim GlobalHttpSlots = new(16, 16);
    private readonly object sync = new();
    private readonly Dictionary<string, Operation> operations = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Budget requests = new(32, 1024 * 1024);
    private readonly Budget datagrams = new(240, 1024 * 1024);
    private static readonly Budget GlobalDatagrams = new(960, 4 * 1024 * 1024);
    private volatile bool disposed;
    private int pendingDatagrams;
    private TaskCompletionSource? datagramsDrained;

    public JsonObject List() { Demand(); return selections.List(package, grant); }

    public string Request(JsonObject parameters)
    {
        Demand();
        var destination = selections.Resolve(package, grant, Contract.Text(parameters, "destinationId", 64));
        Contract.Require(destination.Destination.Scheme is "http" or "https", "invalid_request", "Select an approved HTTP service.");
        string method = Contract.Text(parameters, "method", 16);
        Contract.Require(method is "GET" or "HEAD" or "POST" or "PUT" or "PATCH" or "DELETE" or "OPTIONS", "invalid_request", "Unsupported HTTP method.");
        string path = Contract.Text(parameters, "path", 2048);
        Contract.Require(path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal) && !path.Contains('\\') &&
            !path.Contains('#') && !path.Any(char.IsControl), "invalid_request", "Use a service-relative path beginning with a single slash.");
        Contract.Require(Uri.TryCreate(destination.Destination.Origin + path, UriKind.Absolute, out var target) &&
            target.Scheme == destination.Destination.Scheme && target.IdnHost.Trim('[', ']').Equals(destination.Destination.Host, StringComparison.OrdinalIgnoreCase) &&
            target.Port == destination.Destination.Port && target.UserInfo.Length == 0,
            "invalid_request", "The request must remain at its approved service.");
        byte[] body = Decode(parameters, MaxRequestBytes);
        Contract.Require(method is not ("GET" or "HEAD") || body.Length == 0, "invalid_request", "GET and HEAD requests cannot include a body.");
        Contract.Require(parameters["headers"] is JsonObject { Count: <= 16 }, "invalid_request", "Use at most sixteen request headers.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int headerBytes = 0;
        foreach (var (name, node) in (JsonObject)parameters["headers"]!)
        {
            Contract.Require(node is JsonValue value && value.TryGetValue<string>(out _), "invalid_request", "Header values must be strings.");
            string text = node!.GetValue<string>(); ValidateHeader(name, text);
            Contract.Require(headers.TryAdd(name, text), "invalid_request", "Duplicate request header.");
            headerBytes += name.Length + text.Length;
        }
        Contract.Require(headerBytes <= 8192, "invalid_request", "Request headers exceed 8 KiB.");
        Contract.Require(parameters["useCredential"] is JsonValue credential && credential.TryGetValue<bool>(out _), "invalid_request", "Choose whether to use the approved credential.");
        bool useCredential = parameters["useCredential"]!.GetValue<bool>();
        if (useCredential)
        {
            grant.Demand("credentials.use");
            Contract.Require(destination.ProtectedCredential is not null, "credential_not_granted", "No credential is saved for this destination.");
            Contract.Require(!headers.ContainsKey(destination.CredentialHeader!), "invalid_request", "The saved credential owns this request header.");
        }
        lock (sync)
        {
            Demand();
            Contract.Require(operations.Count < 4, "capacity_exceeded", "Read or cancel an existing request before starting another. Maximum: four.");
            requests.Take(body.Length);
            if (useCredential)
            {
                string secret = selections.Credential(package, grant, destination);
                ValidateHeader(destination.CredentialHeader!, secret); headers.Add(destination.CredentialHeader!, secret);
            }
            Contract.Require(GlobalHttpSlots.Wait(0), "capacity_exceeded", "The host has sixteen active HTTP requests.");
            string id = Guid.NewGuid().ToString("N");
            var operation = new Operation(lifetime.Token);
            operation.Stop.CancelAfter(TimeSpan.FromSeconds(15));
            operations.Add(id, operation);
            operation.Task = RunAsync(destination.Destination, target!, method, headers, body, operation.Stop.Token);
            return id;
        }
    }

    public BrokerResponse Result(string id)
    {
        lock (sync)
        {
            Demand();
            Contract.Require(operations.TryGetValue(id, out var operation), "request_not_found", "Request does not belong to this addon instance or was already consumed.");
            if (!operation.Task.IsCompleted) return new(new JsonObject { ["state"] = "pending", ["byteLength"] = 0 });
            // RunAsync catches expected transport failures. Even an unexpected
            // task failure frees the completed result slot before it propagates.
            operations.Remove(id); operation.Stop.Dispose();
            return operation.Task.GetAwaiter().GetResult();
        }
    }

    public void Cancel(string id)
    {
        lock (sync)
        {
            Demand();
            Contract.Require(operations.TryGetValue(id, out var operation), "request_not_found", "Request does not belong to this addon instance.");
            operation.Stop.Cancel();
            // Keep the slot until Result consumes the terminal result or owner
            // cleanup drains it; repeated cancellation cannot multiply work.
        }
    }

    public async Task<int> SendDatagramAsync(JsonObject parameters, CancellationToken token)
    {
        Demand();
        var destination = selections.Resolve(package, grant, Contract.Text(parameters, "destinationId", 64)).Destination;
        Contract.Require(destination.Scheme == "udp", "invalid_request", "Select an approved UDP destination.");
        byte[] bytes = Decode(parameters, MaxDatagramBytes);
        Contract.Require(bytes.Length > 0, "invalid_request", "A datagram must contain at least one byte.");
        lock (sync)
        {
            Demand();
            Contract.Require(pendingDatagrams < 4, "capacity_exceeded", "Too many pending datagrams.");
            datagrams.Take(bytes.Length); GlobalDatagrams.Take(bytes.Length); pendingDatagrams++;
        }
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(250));
            // Each send addresses exactly one reviewed endpoint. Broadcast is
            // disabled, and no listening/discovery API is implied by this grant.
            var address = IPAddress.Parse(destination.Addresses[0]);
            using var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = false };
            return await socket.SendToAsync(bytes, SocketFlags.None, new IPEndPoint(address, destination.Port), deadline.Token);
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException)
        { throw new AddonException("network_unavailable", "The datagram could not be sent within its deadline."); }
        finally
        {
            lock (sync) { if (--pendingDatagrams == 0) datagramsDrained?.TrySetResult(); }
        }
    }

    private static async Task<BrokerResponse> RunAsync(NetworkDestination destination, Uri target, string method,
        Dictionary<string, string> headers, byte[] body, CancellationToken token)
    {
        try
        {
            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false, UseProxy = false, UseCookies = false, Credentials = null,
                AutomaticDecompression = DecompressionMethods.None, MaxResponseHeadersLength = 16,
                ConnectTimeout = TimeSpan.FromSeconds(5), MaxConnectionsPerServer = 1,
                ConnectCallback = (context, cancellation) => ConnectAsync(destination, context.DnsEndPoint, cancellation),
            };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(new HttpMethod(method), target) { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            if (body.Length > 0 || method is "POST" or "PUT" or "PATCH") request.Content = new ByteArrayContent(body);
            foreach (var (name, value) in headers)
                if (!request.Headers.TryAddWithoutValidation(name, value))
                {
                    request.Content ??= new ByteArrayContent([]);
                    Contract.Require(request.Content.Headers.TryAddWithoutValidation(name, value), "invalid_request", "Unsupported content header.");
                }
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            Contract.Require(response.Content.Headers.ContentLength is null or <= MaxResponseBytes || method == "HEAD",
                "response_too_large", "HTTP response exceeds 64 KiB.");
            var responseHeaders = new JsonObject(); int total = 0;
            foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
            {
                string value = string.Join(", ", values);
                if (responseHeaders.Count == 32 || value.Length > 2048 || total + name.Length + value.Length > 8192) continue;
                responseHeaders[name.ToLowerInvariant()] = value; total += name.Length + value.Length;
            }
            using var buffer = new MemoryStream();
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            byte[] chunk = new byte[8192];
            while (true)
            {
                int read = await stream.ReadAsync(chunk, token); if (read == 0) break;
                Contract.Require(buffer.Length + read <= MaxResponseBytes, "response_too_large", "HTTP response exceeds 64 KiB.");
                buffer.Write(chunk, 0, read);
            }
            byte[] result = buffer.ToArray();
            return new(new JsonObject { ["state"] = "completed", ["status"] = (int)response.StatusCode, ["headers"] = responseHeaders, ["byteLength"] = result.Length }, result);
        }
        catch (AddonException error) { return Failure(error.Code, error.Message); }
        catch (OperationCanceledException) { return Failure("network_cancelled", "HTTP request was cancelled or exceeded its 15-second deadline."); }
        catch (Exception error) when (error is HttpRequestException or IOException or SocketException or ArgumentException or InvalidOperationException)
        { return Failure("network_unavailable", "The approved service could not complete this HTTP request."); }
        finally { GlobalHttpSlots.Release(); }
    }

    private static BrokerResponse Failure(string code, string message) => new(new JsonObject
        { ["state"] = "failed", ["error"] = new JsonObject { ["code"] = code, ["message"] = message }, ["byteLength"] = 0 });

    private static async ValueTask<Stream> ConnectAsync(NetworkDestination destination, DnsEndPoint endpoint, CancellationToken token)
    {
        Contract.Require(endpoint.Host.Trim('[', ']').Equals(destination.Host, StringComparison.OrdinalIgnoreCase) && endpoint.Port == destination.Port,
            "destination_not_granted", "Connection destination changed.");
        // ConnectCallback supplies only the TCP stream. SocketsHttpHandler still
        // performs normal HTTPS hostname/certificate validation above it.
        foreach (string value in destination.Addresses)
        {
            token.ThrowIfCancellationRequested();
            var address = IPAddress.Parse(value);
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token); attempt.CancelAfter(TimeSpan.FromSeconds(1));
                await socket.ConnectAsync(new IPEndPoint(address, destination.Port), attempt.Token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception error) when (error is SocketException or OperationCanceledException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException("Approved service addresses are unavailable.");
    }

    internal static void ValidateHeader(string name, string value)
    {
        Contract.Require(name.Length is > 0 and <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') &&
            value.Length <= 4096 && value.All(c => c is >= ' ' and <= '~'), "invalid_request", "Invalid HTTP header name or value.");
        string lower = name.ToLowerInvariant();
        Contract.Require(lower is not ("host" or "content-length" or "connection" or "transfer-encoding" or "te" or "trailer" or "upgrade" or "keep-alive" or "expect") &&
            !lower.StartsWith("proxy-", StringComparison.Ordinal), "invalid_request", "This transport header is managed by AJN.");
    }

    internal static byte[] Decode(JsonObject parameters, int maximum)
    {
        string encoded = Contract.Text(parameters, "bodyBase64", (maximum + 2) / 3 * 4);
        try
        {
            byte[] bytes = Convert.FromBase64String(encoded);
            Contract.Require(bytes.Length <= maximum, "invalid_request", "Network payload exceeds its limit."); return bytes;
        }
        catch (FormatException) { throw new AddonException("invalid_request", "Network payload is not valid base64."); }
    }

    private void Demand()
    {
        grant.Demand("network.connect");
        Contract.Require(!disposed, "owner_closed", "Addon network access is closed.");
    }

    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (sync)
        {
            disposed = true; lifetime.Cancel();
            datagramsDrained ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (pendingDatagrams == 0) datagramsDrained.TrySetResult();
            pending = [.. operations.Values.Select(o => o.Task), datagramsDrained.Task];
        }
        try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) { throw new AddonException("cleanup_pending", "Network operations are still closing. Retry cleanup."); }
        lock (sync)
        {
            foreach (var operation in operations.Values) operation.Stop.Dispose();
            operations.Clear();
        }
    }

    private sealed class Budget(int maximumCalls, int maximumBytes)
    {
        private readonly object gate = new();
        private long started = Stopwatch.GetTimestamp(); private int calls, bytes;
        public void Take(int size)
        {
            lock (gate)
            {
                if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(1)) { started = Stopwatch.GetTimestamp(); calls = bytes = 0; }
                Contract.Require(calls < maximumCalls && bytes + size <= maximumBytes, "network_rate_exceeded", "Network operation or byte rate exceeded. Reduce the rate and retry later.");
                calls++; bytes += size;
            }
        }
    }
}
