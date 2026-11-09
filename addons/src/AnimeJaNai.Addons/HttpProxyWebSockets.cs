using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;

namespace AnimeJaNai.Addons;

public sealed partial class HttpProxyAccess
{
    private async Task TunnelAsync(Operation operation, Plan plan, HashSet<string> sensitive, HttpContext context, CancellationToken token)
    {
        using var handler = NetworkAccess.CreateHandler(plan.Destination.Destination);
        using var invoker = new HttpMessageInvoker(handler);
        using var upstream = new ClientWebSocket();
        upstream.Options.HttpVersion = System.Net.HttpVersion.Version11;
        upstream.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        upstream.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        upstream.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
        upstream.Options.CollectHttpResponseDetails = true;
        var excluded = Excluded(context.Request.Headers.Select(h => new KeyValuePair<string, IEnumerable<string>>(h.Key, h.Value.Select(v => v!))));
        excluded.UnionWith(["Host", "Content-Length", "Expect", "Forwarded", "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto"]);
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in context.Request.Headers)
        {
            if (excluded.Contains(header.Key) || header.Key.StartsWith("sec-websocket-", StringComparison.OrdinalIgnoreCase) ||
                sensitive.Contains(header.Key) && !plan.PassRequest.Contains(header.Key) ||
                plan.Credential && header.Key.Equals(plan.Destination.CredentialHeader, StringComparison.OrdinalIgnoreCase)) continue;
            headers[header.Key] = header.Value.Select(v => v!).ToArray();
        }
        foreach (var header in plan.RequestHeaders)
            if (header.Value is null) headers.Remove(header.Key); else headers[header.Key] = header.Value;
        if (plan.Credential) headers[plan.Destination.CredentialHeader!] = [selections.Credential(package, grant, plan.Destination)];
        using var credentialRequest = new HttpRequestMessage(HttpMethod.Get, plan.Target);
        foreach (var header in headers) AddHeader(credentialRequest, header.Key, header.Value);
        operation.Credential?.Apply(credentialRequest);
        foreach (var header in credentialRequest.Headers) upstream.Options.SetRequestHeader(header.Key, string.Join(header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ? "; " : ", ", header.Value));
        var protocols = context.WebSockets.WebSocketRequestedProtocols;
        Contract.Require(protocols.Count <= 16 && protocols.All(p => p.Length <= 128), "invalid_request", "Too many WebSocket subprotocols.");
        foreach (string protocol in protocols) upstream.Options.AddSubProtocol(protocol);
        var target = new UriBuilder(credentialRequest.RequestUri!) { Scheme = plan.Target.Scheme == "https" ? "wss" : "ws" }.Uri;
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(token);
        connect.CancelAfter(TimeSpan.FromSeconds(15));
        await upstream.ConnectAsync(target, invoker, connect.Token);
        // Framework handshake fields remain owned by the two WebSocket stacks.
        using var handshake = new HttpResponseMessage(System.Net.HttpStatusCode.SwitchingProtocols);
        foreach (var header in upstream.HttpResponseHeaders ?? new Dictionary<string, IEnumerable<string>>())
            if (!header.Key.StartsWith("sec-websocket-", StringComparison.OrdinalIgnoreCase)) handshake.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var header in ResponseHeaders(handshake, plan)) context.Response.Headers[header.Key] = new Microsoft.Extensions.Primitives.StringValues(header.Value);
        using var downstream = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            SubProtocol = upstream.SubProtocol,
            KeepAliveInterval = TimeSpan.FromSeconds(30), KeepAliveTimeout = TimeSpan.FromSeconds(20)
        });
        lock (sync) operation.Status = 101;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task send = PumpSocketAsync(downstream, upstream, stop.Token);
        Task receive = PumpSocketAsync(upstream, downstream, stop.Token);
        try
        {
            await await Task.WhenAny(send, receive);
            // Allow the other close frame to travel before cancelling a peer
            // that did not acknowledge it. Ordinary data waits use ping/pong.
            await Task.WhenAll(send, receive).WaitAsync(TimeSpan.FromSeconds(5), token);
        }
        finally
        {
            stop.Cancel(); upstream.Abort(); downstream.Abort();
            try { await Task.WhenAll(send, receive); } catch (Exception e) when (e is OperationCanceledException or WebSocketException) { }
        }
    }
    private static async Task PumpSocketAsync(WebSocket source, WebSocket destination, CancellationToken token)
    {
        byte[] chunk = new byte[32768]; long transferred = 0, messageBytes = 0;
        using var writeDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        while (true)
        {
            var read = await source.ReceiveAsync(chunk.AsMemory(), token);
            writeDeadline.CancelAfter(TimeSpan.FromSeconds(15));
            if (read.MessageType == WebSocketMessageType.Close)
            {
                // Never echo an arbitrary upstream close reason into diagnostics.
                await destination.CloseOutputAsync(source.CloseStatus ?? WebSocketCloseStatus.NormalClosure, source.CloseStatusDescription, writeDeadline.Token);
                return;
            }
            transferred += read.Count; messageBytes += read.Count;
            Contract.Require(transferred <= MaximumTransferBytes && messageBytes <= 16 * 1024 * 1024,
                "transfer_limit_reached", "WebSocket transfer or message limit reached.");
            await destination.SendAsync(chunk.AsMemory(0, read.Count), read.MessageType, read.EndOfMessage, writeDeadline.Token);
            writeDeadline.CancelAfter(Timeout.InfiniteTimeSpan);
            if (read.EndOfMessage) messageBytes = 0;
        }
    }
}
