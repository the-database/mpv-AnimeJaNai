using System.Net;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.TestSupport;

internal static partial class Checks
{
    private static async Task HttpProxyChecks()
    {
        await Test("WebSocket forwarding preserves fragmented binary messages and completes close in both directions", async () =>
        {
            var package = Package(permissions: ["network.listen", "network.connect", "network.proxy"]);
            var grant = new PermissionGrant(package, package.Manifest.Permissions); string area = Area();
            int port = ListenerPort(), upstreamPort = ListenerPort(); var selections = new ListenerSelections(area);
            string first = selections.Approve(package, grant, "downstream", new("127.0.0.1", port, [], []));
            string second = selections.Approve(package, grant, "upstream", new("127.0.0.1", upstreamPort, [], []));
            var destinations = new NetworkSelections(area);
            string destination = destinations.Approve(package, grant, "upstream", LoopbackDestination(upstreamPort, host: "127.0.0.1"));
            await using var broker = new Broker(package, grant, area, networkSelections: destinations);
            await using var upstream = new HttpServerAccess(package, grant, selections);
            string server = broker.HttpServers.Open(first), other = upstream.Open(second);
            await Until(() => broker.HttpServers.Status(server)["state"]!.GetValue<string>() == "listening" && upstream.Status(other)["state"]!.GetValue<string>() == "listening");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var client = new System.Net.WebSockets.ClientWebSocket();
            client.Options.AddSubProtocol("test.v1");
            var connecting = client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/socket"), deadline.Token);
            var request = (await NextHttp(broker.HttpServers)).Data;
            True(request["webSocketRequested"]!.GetValue<bool>());
            string operation = broker.HttpProxy.Forward(request["requestId"]!.GetValue<string>(), destination, new() { ["path"] = "/echo" });
            string incoming = (await NextHttp(upstream)).Data["requestId"]!.GetValue<string>();
            Task echo = upstream.ClaimNative(incoming, async (context, token) =>
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync("test.v1");
                byte[] bytes = new byte[17000];
                while (true)
                {
                    var read = await socket.ReceiveAsync(bytes.AsMemory(), token);
                    if (read.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                    {
                        await socket.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "done", token); return;
                    }
                    await socket.SendAsync(bytes.AsMemory(0, read.Count), read.MessageType, read.EndOfMessage, token);
                }
            });
            await connecting; True(client.SubProtocol == "test.v1");
            byte[] payload = Enumerable.Range(0, 100_000).Select(i => (byte)i).ToArray();
            await client.SendAsync(payload.AsMemory(0, 60000), System.Net.WebSockets.WebSocketMessageType.Binary, false, deadline.Token);
            await client.SendAsync(payload.AsMemory(60000), System.Net.WebSockets.WebSocketMessageType.Binary, true, deadline.Token);
            using var received = new MemoryStream(); byte[] chunk = new byte[10001];
            while (true)
            {
                var read = await client.ReceiveAsync(chunk.AsMemory(), deadline.Token);
                True(read.MessageType == System.Net.WebSockets.WebSocketMessageType.Binary);
                received.Write(chunk, 0, read.Count); if (read.EndOfMessage) break;
            }
            True(received.ToArray().SequenceEqual(payload));
            await client.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "done", deadline.Token);
            await echo;
            await Until(() => broker.HttpProxy.Status(operation)["cleanupReady"]!.GetValue<bool>());
            True(broker.HttpProxy.Status(operation)["state"]!.GetValue<string>() == "completed", broker.HttpProxy.Status(operation).ToJsonString());
            broker.HttpProxy.Close(operation);
        });
        await Test("Native proxy streams large bodies to pinned destinations and preserves response bytes and repeated headers", async () =>
        {
            byte[] payload = Enumerable.Range(0, 700_000).Select(i => (byte)i).ToArray();
            await using var upstream = new HttpFixture((_, _) => Task.FromResult(new HttpReply(206, payload,
                "Content-Encoding: gzip\r\nSet-Cookie: a=1\r\nSet-Cookie: b=2\r\nContent-Range: bytes 0-699999/800000\r\n", Chunked: true)), maximumBody: 900_000);
            var package = Package(permissions: ["network.listen", "network.connect", "network.proxy"]);
            var grant = new PermissionGrant(package, package.Manifest.Permissions); string area = Area();
            var destinations = new NetworkSelections(area); var listeners = new ListenerSelections(area); int port = ListenerPort();
            string destination = destinations.Approve(package, grant, "upstream", LoopbackDestination(upstream.Port));
            string selected = listeners.Approve(package, grant, "test", new("127.0.0.1", port, ["X-Secret"], []));
            await using var broker = new Broker(package, grant, area, networkSelections: destinations);
            var access = broker.HttpServers; string server = access.Open(selected);
            await Until(() => access.Status(server)["state"]!.GetValue<string>() == "listening");
            using var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
            using var input = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/upload") { Content = new ByteArrayContent(payload) };
            input.Headers.Add("X-Secret", "private"); input.Headers.Add("Cookie", "client=one"); input.Headers.Add("Range", "bytes=0-699999");
            var sent = client.SendAsync(input);
            string request = (await NextHttp(access)).Data["requestId"]!.GetValue<string>();
            var options = new JsonObject { ["path"] = "/asset?q=a&q=b", ["passRequestHeaders"] = new JsonArray("Cookie"), ["passResponseHeaders"] = new JsonArray("Set-Cookie") };
            string operation = broker.HttpProxy.Forward(request, destination, options);
            try { access.Respond(request, 204, [], []); throw new Exception("Second response owner admitted"); }
            catch (AddonException e) when (e.Code is "request_claimed" or "request_not_found") { }
            using var response = await sent;
            True(response.StatusCode == HttpStatusCode.PartialContent);
            True((await response.Content.ReadAsByteArrayAsync()).SequenceEqual(payload));
            True(response.Content.Headers.ContentEncoding.Single() == "gzip" && response.Headers.GetValues("Set-Cookie").Count() == 2);
            True(upstream.Last!.Body.SequenceEqual(payload) && upstream.Last.Path == "/asset?q=a&q=b");
            True(upstream.Last.Headers["Host"] == $"device.invalid:{upstream.Port}" && !upstream.Last.Headers.ContainsKey("X-Secret") && upstream.Last.Headers["Cookie"] == "client=one");
            await Until(() => broker.HttpProxy.Status(operation)["cleanupReady"]!.GetValue<bool>());
            broker.HttpProxy.Close(operation);
        });
        await Test("Buffered proxy results are all-or-error, chunked and independently owned", async () =>
        {
            byte[] payload = Enumerable.Range(0, 130_001).Select(i => (byte)(i * 31)).ToArray();
            await using var upstream = new HttpFixture((_, _) => Task.FromResult(new HttpReply(200, payload, Chunked: true)));
            var package = Package(permissions: ["network.connect", "network.proxy"]); var grant = new PermissionGrant(package, package.Manifest.Permissions);
            string area = Area(); var destinations = new NetworkSelections(area);
            string destination = destinations.Approve(package, grant, "upstream", LoopbackDestination(upstream.Port));
            await using var broker = new Broker(package, grant, area, networkSelections: destinations);
            string operation = broker.HttpProxy.Buffer(destination, new() { ["path"] = "/", ["maximumBytes"] = 200_000L });
            await Until(() => broker.HttpProxy.Status(operation)["cleanupReady"]!.GetValue<bool>());
            True(broker.HttpProxy.Status(operation)["state"]!.GetValue<string>() == "completed");
            using var received = new MemoryStream();
            for (int offset = 0; offset < payload.Length; offset += 32768)
            {
                var read = await broker.InvokeTransportAsync("httpProxy.read", new() { ["operationId"] = operation, ["offset"] = (long)offset, ["count"] = 32768L }, default);
                received.Write(read.Binary.Span);
            }
            True(received.ToArray().SequenceEqual(payload)); broker.HttpProxy.Close(operation);
            await Error("operation_not_found", () => broker.HttpProxy.Read(operation, 0, 1));
            string large = broker.HttpProxy.Buffer(destination, new() { ["path"] = "/", ["maximumBytes"] = 100_000L });
            await Until(() => broker.HttpProxy.Status(large)["cleanupReady"]!.GetValue<bool>());
            True(broker.HttpProxy.Status(large)["error"]!["code"]!.GetValue<string>() == "response_too_large");
            await Error("body_not_ready", () => broker.HttpProxy.Read(large, 0, 1)); broker.HttpProxy.Close(large);
        });
        await Test("Proxy redirects are returned without following and header framing cannot be overridden", async () =>
        {
            await using var upstream = new HttpFixture((_, _) => Task.FromResult(new HttpReply(302, [], "Location: http://not-approved.invalid/\r\n")));
            var package = Package(permissions: ["network.connect", "network.proxy"]); var grant = new PermissionGrant(package, package.Manifest.Permissions);
            string area = Area(); var destinations = new NetworkSelections(area);
            string destination = destinations.Approve(package, grant, "upstream", LoopbackDestination(upstream.Port));
            await using var broker = new Broker(package, grant, area, networkSelections: destinations);
            foreach (string header in new[] { "Host", "Connection", "Content-Length", "Transfer-Encoding", "Access-Control-Allow-Origin" })
                await Error("invalid_request", () => broker.HttpProxy.Buffer(destination, new() { ["path"] = "/", ["requestHeaders"] = new JsonArray(new JsonObject { ["name"] = header, ["values"] = new JsonArray("invalid") }) }));
            string operation = broker.HttpProxy.Buffer(destination, new() { ["path"] = "/" });
            await Until(() => broker.HttpProxy.Status(operation)["cleanupReady"]!.GetValue<bool>());
            True(broker.HttpProxy.Status(operation)["status"]!.GetValue<int>() == 302 && upstream.Requests == 1);
            broker.HttpProxy.Close(operation);
        });
    }
}
