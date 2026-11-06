using AnimeJaNai.Addons;
using AnimeJaNai.Addons.TestSupport;
using System.Text.Json.Nodes;

internal static partial class Checks
{
    private static async Task HttpBridgeRuntimeChecks(string runtime, string compiler, string dotnet)
    {
        await Test("Packaged Wasm bridge tunnels WebSockets and owner shutdown closes both connections", async () =>
        {
            string area = Area(), source = Path.Combine(area, "source"), data = Path.Combine(area, "data");
            DeveloperTools.New(source, "org.example.wsbridge");
            foreach (string file in new[] { "manifest.json", "addon.js" }) File.Copy(Path.Combine(AppContext.BaseDirectory, "http-bridge", file), Path.Combine(source, file), true);
            string archive = Path.Combine(area, "bridge.ajnaddon"); await DeveloperTools.BuildAsync(source, compiler, archive);
            var package = AddonPackage.Load(archive); var grant = new PermissionGrant(package, package.Manifest.Permissions);
            int port = ListenerPort(), upstreamPort = ListenerPort(); var listeners = new ListenerSelections(data);
            listeners.Approve(package, grant, "client", new("127.0.0.1", port, [], []));
            var upstreamSelections = new ListenerSelections(Path.Combine(area, "upstream"));
            string upstreamSelection = upstreamSelections.Approve(package, grant, "upstream", new("127.0.0.1", upstreamPort, [], []));
            await using var upstream = new HttpServerAccess(package, grant, upstreamSelections);
            string server = upstream.Open(upstreamSelection);
            await Until(() => upstream.Status(server)["state"]!.GetValue<string>() == "listening");
            var destinations = new NetworkSelections(data); destinations.Approve(package, grant, "upstream", LoopbackDestination(upstreamPort, host: "127.0.0.1"));
            await using var worker = await AddonWorker.StartAsync(package, grant, runtime, Path.Combine(area, "workers"), data,
                new WorkerCommand(Path.GetFullPath(dotnet), [typeof(AddonWorker).Assembly.Location]), networkSelections: destinations);
            await worker.SendEventAsync("start"); await worker.SendEventAsync("action", new JsonObject { ["id"] = "open" });
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while ((await worker.SendEventAsync("action", new JsonObject { ["id"] = "status" }))?["state"]?.GetValue<string>() != "listening") await Task.Delay(25, deadline.Token);
            using var client = new System.Net.WebSockets.ClientWebSocket(); client.Options.AddSubProtocol("test.v1");
            Task connecting = client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/proxy"), deadline.Token);
            string incoming = (await NextHttp(upstream)).Data["requestId"]!.GetValue<string>();
            Task echo = upstream.ClaimNative(incoming, async (context, token) =>
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync("test.v1"); byte[] chunk = new byte[8192];
                try
                {
                    while (true)
                    {
                        var read = await socket.ReceiveAsync(chunk.AsMemory(), token);
                        if (read.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) return;
                        await socket.SendAsync(chunk.AsMemory(0, read.Count), read.MessageType, read.EndOfMessage, token);
                    }
                }
                catch (Exception e) when (e is System.Net.WebSockets.WebSocketException or OperationCanceledException) { }
            });
            await connecting;
            byte[] payload = Enumerable.Range(0, 100000).Select(i => (byte)i).ToArray();
            await client.SendAsync(payload.AsMemory(), System.Net.WebSockets.WebSocketMessageType.Binary, true, deadline.Token);
            using var received = new MemoryStream(); byte[] buffer = new byte[8192];
            while (true)
            {
                var read = await client.ReceiveAsync(buffer.AsMemory(), deadline.Token); received.Write(buffer, 0, read.Count);
                if (read.EndOfMessage) break;
            }
            True(received.ToArray().SequenceEqual(payload) && client.SubProtocol == "test.v1");
            await worker.DisposeAsync(); await echo.WaitAsync(TimeSpan.FromSeconds(10));
            try { var closed = await client.ReceiveAsync(buffer.AsMemory(), deadline.Token); True(closed.MessageType == System.Net.WebSockets.WebSocketMessageType.Close); }
            catch (System.Net.WebSockets.WebSocketException) { }
        });
        await Test("Packaged Wasm bridge round-trips buffered bodies and native proxy responses through public SDK", async () =>
        {
            string area = Area(), source = Path.Combine(area, "source"), data = Path.Combine(area, "data");
            DeveloperTools.New(source, "org.example.bridge");
            foreach (string file in new[] { "manifest.json", "addon.js" }) File.Copy(Path.Combine(AppContext.BaseDirectory, "http-bridge", file), Path.Combine(source, file), true);
            string archive = Path.Combine(area, "bridge.ajnaddon"); await DeveloperTools.BuildAsync(source, compiler, archive);
            var package = AddonPackage.Load(archive); var grant = new PermissionGrant(package, package.Manifest.Permissions);
            byte[] payload = Enumerable.Range(0, 200_007).Select(i => (byte)(i * 7)).ToArray();
            var authorizations = new System.Collections.Concurrent.ConcurrentQueue<string>();
            await using var upstream = new HttpFixture(async (request, token) =>
            {
                if (request.Headers.TryGetValue("X-Upstream-Key", out var key))
                {
                    True(!request.Headers.ContainsKey("X-Client-Key"));
                    if (request.Path == "/account")
                    {
                        authorizations.Enqueue(key); await Task.Delay(100, token);
                        return new HttpReply(key is "client-A" or "client-B" ? 200 : 403, System.Text.Encoding.UTF8.GetBytes("{\"allowed\":true}"));
                    }
                    return new HttpReply(200, System.Text.Encoding.UTF8.GetBytes(key == "client-A" ? "account-A-content" : "account-B-content"));
                }
                return new HttpReply(200, payload, Chunked: true);
            });
            var destinations = new NetworkSelections(data); destinations.Approve(package, grant, "upstream", LoopbackDestination(upstream.Port));
            int port = ListenerPort(); new ListenerSelections(data).Approve(package, grant, "loopback", new("127.0.0.1", port, [], []));
            await using var worker = await AddonWorker.StartAsync(package, grant, runtime, Path.Combine(area, "workers"), data,
                new WorkerCommand(Path.GetFullPath(dotnet), [typeof(AddonWorker).Assembly.Location]), networkSelections: destinations);
            await worker.SendEventAsync("start"); await worker.SendEventAsync("action", new JsonObject { ["id"] = "open" });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                var status = await worker.SendEventAsync("action", new JsonObject { ["id"] = "status" }, timeout.Token);
                if (status?["state"]?.GetValue<string>() == "listening") break;
                await Task.Delay(25, timeout.Token);
            }
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var control = await client.PostAsync($"http://127.0.0.1:{port}/control", new ByteArrayContent(payload));
            True(control.IsSuccessStatusCode && (await control.Content.ReadAsByteArrayAsync()).SequenceEqual(payload), worker.Diagnostics);
            foreach (string route in new[] { "proxy", "metadata" })
            {
                using var response = await client.GetAsync($"http://127.0.0.1:{port}/{route}");
                True(response.IsSuccessStatusCode && (await response.Content.ReadAsByteArrayAsync()).SequenceEqual(payload), route + ": " + worker.Diagnostics);
            }
            async Task<string> Client(string key)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/authorized-proxy"); request.Headers.Add("X-Client-Key", key);
                using var response = await client.SendAsync(request); True(response.IsSuccessStatusCode, worker.Diagnostics);
                return await response.Content.ReadAsStringAsync();
            }
            var clients = await Task.WhenAll(Client("client-A"), Client("client-B"));
            True(clients[0] == "account-A-content" && clients[1] == "account-B-content", "Request credentials crossed client boundaries.");
            True(authorizations.Contains("client-A") && authorizations.Contains("client-B"));
            using var deniedRequest = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/authorized-proxy"); deniedRequest.Headers.Add("X-Client-Key", "denied-client");
            using var denied = await client.SendAsync(deniedRequest); True(denied.StatusCode == System.Net.HttpStatusCode.Forbidden);
            True(!worker.Diagnostics.Contains("client-A") && !worker.Diagnostics.Contains("client-B") && !worker.Diagnostics.Contains("denied-client"));
            await worker.SendEventAsync("action", new JsonObject { ["id"] = "close" });
            await worker.SendEventAsync("stop");
        });
    }
}
